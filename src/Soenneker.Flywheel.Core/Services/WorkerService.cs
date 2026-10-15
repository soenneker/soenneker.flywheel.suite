using Soenneker.Flywheel.Core.Services.Abstract;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Asyncs.Locks;
using Soenneker.Atomics.ValueInts;
using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Services;

public sealed class WorkerService(IJobExecutor executor, IJobStore store, FlywheelOptions options, ILogger<WorkerService> logger) : BackgroundService, IWorkerPool
{
    private readonly AsyncLock _lock = new();
    private readonly List<WorkerState> _workers = [];
    private readonly Dictionary<string, WorkerPoolState> _pools = CreatePools(options);

    private static Dictionary<string, WorkerPoolState> CreatePools(FlywheelOptions options)
    {
        options.Validate();
        Dictionary<string, WorkerPoolState> pools = options.Pools.ToDictionary(pair => pair.Key,
            pair => new WorkerPoolState(pair.Key, pair.Value), StringComparer.Ordinal);
        pools.Add("default", new WorkerPoolState("default", options.Workers - options.Pools.Values.Sum()));
        return pools;
    }
    private CancellationToken _stoppingToken;
    private ValueAtomicInt _started;
    private readonly CancellationTokenSource _executionStop = new();
    private int _draining;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await Drain();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.ShutdownGracePeriod);
        using var registration = deadline.Token.UnsafeRegister(state => { _ = CancelExecutions(); }, null);
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        Interlocked.Exchange(ref _draining, 1);
        _ = CancelExecutions();
        base.Dispose();
        // Dispose after workers exit; an early host shutdown deadline can leave them returning their leases.
        _ = DisposeExecutionStop();
    }

    private async Task DisposeExecutionStop()
    {
        try { if (ExecuteTask is not null) await ExecuteTask; }
        catch (Exception) { /* ExecuteTask is also observed by the host. */ }
        finally { _executionStop.Dispose(); }
    }

    private async Task CancelExecutions()
    {
        try { await _executionStop.CancelAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "Worker shutdown cancellation failed"); }
    }

    private async Task Drain()
    {
        Interlocked.Exchange(ref _draining, 1);
        using (await _lock.Lock())
            foreach (WorkerState worker in _workers)
            {
                worker.Retiring.Write(1);
                worker.Wake.Cancel();
            }
    }

    public int WorkerCount => _pools.Values.Sum(pool => pool.WorkerCount.Read());
    public IReadOnlyList<WorkerPoolCapacity> Pools => _pools.Values
        .Select(pool => new WorkerPoolCapacity(pool.Name, pool.WorkerCount.Read())).ToArray();

    public ValueTask SetWorkerCount(int count, CancellationToken cancellationToken = default) =>
        _pools.Count == 1 ? SetPoolWorkerCount(_pools.Keys.Single(), count, cancellationToken)
            : throw new InvalidOperationException("Select a pool with SetPoolWorkerCount when multiple pools are configured.");

    public async ValueTask SetPoolWorkerCount(string pool, int count, CancellationToken cancellationToken = default)
    {
        if (!_pools.TryGetValue(pool, out WorkerPoolState? state)) throw new ArgumentException("Unknown worker pool.", nameof(pool));
        if (count is < 0 or > 256) throw new ArgumentOutOfRangeException(nameof(count));
        using (await _lock.Lock(cancellationToken))
        {
            if (WorkerCount - state.WorkerCount.Read() + count > (_pools.Count == 1 ? 256 : options.Workers)) throw new ArgumentOutOfRangeException(nameof(count));
            state.WorkerCount.Write(count);
            if (_started.Read() != 0) ResizeLocked();
        }
        state.Pulse();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        using (await _lock.Lock(stoppingToken))
        {
            _started.Write(1);
            ResizeLocked();
        }
        Pulse();

        Task notifications = Watch(stoppingToken);
        Task recovery = Recover(stoppingToken);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        Pulse();
        Task[] workers;
        using (await _lock.Lock()) workers = _workers.Select(x => x.Task).ToArray();
        await Task.WhenAll(workers.Append(notifications).Append(recovery));
    }

    private void ResizeLocked()
    {
        if (Volatile.Read(ref _draining) != 0 || _stoppingToken.IsCancellationRequested) return;
        foreach (WorkerPoolState pool in _pools.Values) ResizePoolLocked(pool);
    }

    private void ResizePoolLocked(WorkerPoolState pool)
    {
        int desired = pool.WorkerCount.Read();
        WorkerState[] available = _workers.Where(x => x.Pool == pool && x.Retiring.Read() == 0).ToArray();
        for (int i = desired; i < available.Length; i++)
        {
            available[i].Retiring.Write(1);
            available[i].Wake.Cancel();
        }
        for (int i = available.Length; i < desired; i++)
        {
            var worker = new WorkerState(pool, _stoppingToken);
            _workers.Add(worker);
            worker.Task = Task.Run(() => Run(worker, _stoppingToken), CancellationToken.None);
        }
    }

    private async Task Run(WorkerState worker, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && worker.Retiring.Read() == 0)
            {
                // One idle worker probes storage. A successful claim wakes the next worker before execution,
                // filling the pool without having every idle worker race the same revision or empty queue.
                try { await worker.Pool.Pending.Reader.ReadAsync(worker.Wake.Token); }
                catch (OperationCanceledException) when (worker.Wake.IsCancellationRequested) { break; }
                if (worker.Retiring.Read() != 0) break;
                try { if (await executor.RunOnce(worker.Pool.Name, worker.Pool.Pulse, token, _executionStop.Token)) worker.Pool.Pulse(); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (JobExecutionUnresponsiveException ex)
                {
                    logger.LogCritical(ex, "Worker pool stopped because a handler ignored cancellation");
                    await Drain();
                    await CancelExecutions();
                    break;
                }
                catch (Exception ex) { logger.LogError(ex, "Worker storage failure"); }
            }
        }
        finally
        {
            using (await _lock.Lock())
            {
                _workers.Remove(worker);
                if (!token.IsCancellationRequested && Volatile.Read(ref _draining) == 0)
                {
                    ResizeLocked();
                    worker.Pool.Pulse(); // Preserve a hint consumed by a worker racing retirement.
                }
            }
            worker.Wake.Dispose();
        }
    }

    private async Task Watch(CancellationToken token)
    {
        if (store is not IJobChangeFeed feed) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await foreach (JobChange change in feed.Watch(token))
                    if (change.Kind is "Job" or "Resync") Pulse();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Worker change subscription unavailable: {Error}", ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task Recover(CancellationToken token)
    {
        using var timer = new PeriodicTimer(options.PollInterval);
        try { while (await timer.WaitForNextTickAsync(token)) Pulse(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Pulse()
    {
        foreach (WorkerPoolState pool in _pools.Values) pool.Pulse();
    }
}
