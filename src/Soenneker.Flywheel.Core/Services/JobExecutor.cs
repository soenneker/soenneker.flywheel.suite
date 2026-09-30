using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Core.Services.Abstract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Logging;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Core.Services;

public sealed class JobExecutor(IJobStore store, IServiceScopeFactory scopes, IEnumerable<IJobInvoker> invokers,
    FlywheelOptions options, ILogger<JobExecutor> logger, JobLogCapture? logCapture = null, IJobLogStore? logStore = null,
    JobProgress? progress = null, IHostApplicationLifetime? lifetime = null) : IJobExecutor
{
    private readonly Dictionary<string, IJobInvoker> _invokers = invokers.ToDictionary(x => x.Name, StringComparer.Ordinal);
    private readonly JobProgress _progress = progress ?? new JobProgress(store);
    private int _unhealthy;

    public Task<bool> RunOnce(CancellationToken cancellationToken) => RunOnceCore(null, cancellationToken, cancellationToken);
    public Task<bool> RunOnce(Action onClaimed, CancellationToken cancellationToken) => RunOnceCore(onClaimed, cancellationToken, cancellationToken);
    public Task<bool> RunOnce(Action onClaimed, CancellationToken claimingToken, CancellationToken executionToken) =>
        RunOnceCore(onClaimed, claimingToken, executionToken);

    private async Task<bool> RunOnceCore(Action? onClaimed, CancellationToken claimingToken, CancellationToken executionToken)
    {
        if (Volatile.Read(ref _unhealthy) != 0) throw new JobExecutionUnresponsiveException("previous execution");
        if (store is not IWorkerJobStore workerStore)
            throw new NotSupportedException("Flywheel workers require IWorkerJobStore for handler eligibility and interrupted execution recovery.");
        if (_invokers.Count == 0) return false;
        JobLease? lease;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(claimingToken))
        {
            deadline.CancelAfter(options.GetStorageOperationTimeout());
            lease = await workerStore.ClaimForWorker(options.NodeId, options.LeaseDuration, options.ApplicationVersion,
                _invokers.Keys.ToHashSet(StringComparer.Ordinal), deadline.Token);
        }
        if (lease is null) return false;
        onClaimed?.Invoke();
        await ExecuteLease(lease, workerStore, executionToken);
        return true;
    }

    private async Task ExecuteLease(JobLease lease, IWorkerJobStore workerStore, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(lease.Job.Policy.Timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        // Keep the handler token separate: a throwing or blocked cancellation callback must not block the watchdog.
        var execution = new CancellationTokenSource();
        using var cancellationRegistration = stop.Token.UnsafeRegister(state => { _ = CancelExecution(); }, null);
        using var renewal = new CancellationTokenSource();
        var lost = 0;
        var cancelled = 0;
        var unresponsive = false;
        var interrupted = false;
        Task renewTask = Renew();
        JobOutcome outcome = Communication.Enums.JobOutcome.Succeeded;
        string? error = null;
        JobLogCapture.Session? logSession = logCapture is not null && logStore is not null ? logCapture.Begin(lease, logStore) : null;
        logSession?.Write("Information", "Flywheel", $"Starting attempt {lease.Job.Attempt} on server {options.NodeId}; timeout {lease.Job.Policy.Timeout}.");
        if (lease.Job.Attempt > 1) logSession?.Write("Information", "Flywheel", $"Retrying with attempt {lease.Job.Attempt} on server {options.NodeId}.");
        // The invocation owns its scope until it actually exits, even when cancellation is ignored.
        Task invocation = Task.Run(async () =>
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            using IDisposable progressScope = _progress.Begin(lease);
            execution.Token.ThrowIfCancellationRequested();
            await _invokers[lease.Job.Name].Invoke(scope.ServiceProvider, lease.Job.Payload, execution.Token);
            execution.Token.ThrowIfCancellationRequested();
        }, CancellationToken.None);
        try
        {
            Task stopRequested = Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
            if (await Task.WhenAny(invocation, stopRequested) != invocation)
            {
                try { await invocation.WaitAsync(options.CancellationGracePeriod); }
                catch (TimeoutException) when (!invocation.IsCompleted)
                {
                    unresponsive = true;
                    Interlocked.Exchange(ref _unhealthy, 1);
                    error = $"Handler did not stop within {options.CancellationGracePeriod} of cancellation; execution quarantined, host restart required.";
                    outcome = Communication.Enums.JobOutcome.Failed;
                    logger.LogCritical("Job {JobId} ignored cancellation; stopping this worker host", lease.Job.Id);
                }
            }
            else await invocation;
        }
        catch (Exception ex)
        {
            outcome = Communication.Enums.JobOutcome.Failed;
            interrupted = ex is OperationCanceledException && cancellationToken.IsCancellationRequested &&
                !timeout.IsCancellationRequested && Volatile.Read(ref cancelled) == 0 && Volatile.Read(ref lost) == 0;
            error = ex is not OperationCanceledException ? $"{ex.GetType().Name}: {ex.Message}"
                : Volatile.Read(ref cancelled) != 0 ? "Cancellation requested"
                : Volatile.Read(ref lost) != 0 ? "Execution stopped because its lease was lost"
                : interrupted ? "Execution interrupted by worker shutdown"
                : timeout.IsCancellationRequested ? $"Execution exceeded its configured timeout ({lease.Job.Policy.Timeout})"
                : "A handler or dependency cancelled an operation before the job timeout";
            for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
                logSession?.Write("Warning", "Flywheel", $"{cause.GetType().FullName}: {cause.Message}\n{cause.StackTrace}");
            logger.LogWarning(ex, "Job {JobId} attempt {Attempt} stopped: {Error}", lease.Job.Id, lease.Job.Attempt, error);
        }
        finally
        {
            logSession?.Write("Information", "Flywheel", unresponsive ? error! : $"Handler finished: {outcome.Name}." + (error is null ? "" : $" {error}"));
            if (logSession is not null) await logSession.DisposeAsync();
            await renewal.CancelAsync();
            await renewTask;
            await cancellationRegistration.DisposeAsync();
            if (unresponsive) _ = ObserveAbandonedInvocation();
            else execution.Dispose();
        }
        try
        {
            if (Volatile.Read(ref lost) == 0)
            {
                if (Volatile.Read(ref cancelled) != 0) outcome = Communication.Enums.JobOutcome.Cancelled;
                using var commit = new CancellationTokenSource(options.LeaseDuration / 3);
                bool committed = unresponsive ? await workerStore.Quarantine(lease, error!, commit.Token)
                    : interrupted ? await workerStore.Interrupt(lease, error!, commit.Token)
                    : await store.Finish(lease, outcome, error, lease.Job.Policy.RetryDelay(lease.Job.Attempt, Random.Shared.NextDouble()), commit.Token);
                if (!committed) logger.LogWarning("Job {JobId} outcome was rejected because its lease is no longer owned", lease.Job.Id);
            }
        }
        finally
        {
            if (unresponsive)
            {
                lifetime?.StopApplication();
                throw new JobExecutionUnresponsiveException(lease.Job.Id);
            }
        }

        async Task CancelExecution()
        {
            try { await execution.CancelAsync(); }
            catch (Exception ex) { logger.LogWarning(ex, "Job {JobId} cancellation callback failed", lease.Job.Id); }
        }
        async Task ObserveAbandonedInvocation()
        {
            try { await invocation; }
            catch (Exception ex) { logger.LogWarning(ex, "Quarantined job {JobId} eventually exited", lease.Job.Id); }
            finally { execution.Dispose(); }
        }
        async Task Renew()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(options.LeaseDuration / 3, renewal.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(renewal.Token);
                    deadline.CancelAfter(options.LeaseDuration / 3);
                    LeaseStatus status = await store.Renew(lease, options.LeaseDuration, deadline.Token).WaitAsync(deadline.Token);
                    if (status == Communication.Enums.LeaseStatus.Lost)
                    {
                        Interlocked.Exchange(ref lost, 1);
                        await stop.CancelAsync();
                        return;
                    }
                    if (status == Communication.Enums.LeaseStatus.CancellationRequested)
                    {
                        Interlocked.Exchange(ref cancelled, 1);
                        await stop.CancelAsync();
                    }
                }
            }
            catch (OperationCanceledException) when (renewal.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref lost, 1);
                logger.LogWarning(ex, "Lease renewal failed for {JobId}", lease.Job.Id);
                await stop.CancelAsync();
            }
        }
    }
}
