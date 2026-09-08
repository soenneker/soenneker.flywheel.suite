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
    private TaskCompletionSource _pulse = NewPulse();
    private CancellationToken _stoppingToken;
    private ValueAtomicInt _workerCount = new(options.Workers);
    private ValueAtomicInt _started;

    public int WorkerCount => _workerCount.Read();

    public async ValueTask SetWorkerCount(int count, CancellationToken cancellationToken = default)
    {
        if (count is < 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(count));
        _workerCount.Write(count);
        using (await _lock.Lock(cancellationToken))
            if (_started.Read() != 0) ResizeLocked();
        Pulse();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        using (await _lock.Lock(stoppingToken))
        {
            _started.Write(1);
            ResizeLocked();
        }

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
        int desired = WorkerCount;
        WorkerState[] available = _workers.Where(x => x.Retiring.Read() == 0).ToArray();
        for (int i = desired; i < available.Length; i++)
            available[i].Retiring.Write(1);
        for (int i = available.Length; i < desired; i++)
        {
            var worker = new WorkerState();
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
                Task wake = Volatile.Read(ref _pulse).Task;
                try { if (await executor.RunOnce(token)) continue; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogError(ex, "Worker storage failure"); }
                if (worker.Retiring.Read() != 0) break;
                try { await wake.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            using (await _lock.Lock())
            {
                _workers.Remove(worker);
                if (!token.IsCancellationRequested)
                    ResizeLocked();
            }
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

    private void Pulse() => Interlocked.Exchange(ref _pulse, NewPulse()).TrySetResult();

    private static TaskCompletionSource NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
