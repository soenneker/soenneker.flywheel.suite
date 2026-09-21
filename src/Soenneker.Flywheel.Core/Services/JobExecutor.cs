using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Core.Services.Abstract;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Logging;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Core.Services;

public sealed class JobExecutor(IJobStore store, IServiceScopeFactory scopes, IEnumerable<IJobInvoker> invokers,
    FlywheelOptions options, ILogger<JobExecutor> logger, JobLogCapture? logCapture = null, IJobLogStore? logStore = null,
    JobProgress? progress = null) : IJobExecutor
{
    private readonly Dictionary<string, IJobInvoker> _invokers = invokers.ToDictionary(x => x.Name, StringComparer.Ordinal);
    private readonly JobProgress _progress = progress ?? new JobProgress(store);

    public Task<bool> RunOnce(CancellationToken cancellationToken) => RunOnceCore(null, cancellationToken);

    public Task<bool> RunOnce(Action onClaimed, CancellationToken cancellationToken) => RunOnceCore(onClaimed, cancellationToken);

    private async Task<bool> RunOnceCore(Action? onClaimed, CancellationToken cancellationToken)
    {
        JobLease? lease = store is IVersionedJobStore versioned
            ? await versioned.ClaimForVersion(options.NodeId, options.LeaseDuration, options.ApplicationVersion, cancellationToken)
            : await store.Claim(options.NodeId, options.LeaseDuration, cancellationToken);
        if (lease is null) return false;
        onClaimed?.Invoke();
        await ExecuteLease(lease, cancellationToken);
        return true;
    }

    private async Task ExecuteLease(JobLease lease, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(lease.Job.Policy.Timeout);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var renewal = new CancellationTokenSource();
        var lost = 0;
        var cancelled = 0;
        Task renewTask = Renew();
        JobOutcome outcome = Communication.Enums.JobOutcome.Succeeded;
        string? error = null;
        JobLogCapture.Session? logSession = logCapture is not null && logStore is not null ? logCapture.Begin(lease, logStore) : null;
        logSession?.Write("Information", "Flywheel", $"Starting attempt {lease.Job.Attempt} on {options.NodeId}; timeout {lease.Job.Policy.Timeout}." );
        if (lease.Job.Attempt > 1)
            logSession?.Write("Information", "Flywheel", $"Retrying with attempt {lease.Job.Attempt} on {options.NodeId}.");
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (!_invokers.TryGetValue(lease.Job.Name, out IJobInvoker? invoker))
                throw new InvalidOperationException($"No generated registration for {lease.Job.Name}");
            using IDisposable progressScope = _progress.Begin(lease);
            await invoker.Invoke(scope.ServiceProvider, lease.Job.Payload, execution.Token);
            execution.Token.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            outcome = Communication.Enums.JobOutcome.Failed;
            error = ex is not OperationCanceledException ? $"{ex.GetType().Name}: {ex.Message}"
                : Volatile.Read(ref cancelled) != 0 ? "Cancellation requested"
                : Volatile.Read(ref lost) != 0 ? "Execution stopped because its lease was lost"
                : cancellationToken.IsCancellationRequested ? "Execution interrupted by worker shutdown"
                : timeout.IsCancellationRequested ? $"Execution exceeded its configured timeout ({lease.Job.Policy.Timeout})"
                : "A handler or dependency cancelled an operation before the job timeout";
            // Include each cause's message so stored job logs explain the failure as well as its call site.
            for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
                logSession?.Write("Warning", "Flywheel", $"{cause.GetType().FullName}: {cause.Message}\n{cause.StackTrace}");
            logger.LogWarning(ex, "Job {JobId} attempt {Attempt} failed: {Error}", lease.Job.Id, lease.Job.Attempt, error);
        }
        finally
        {
            logSession?.Write("Information", "Flywheel", $"Handler finished: {outcome.Name}." + (error is null ? "" : $" {error}"));
            if (logSession is not null) await logSession.DisposeAsync();
            await renewal.CancelAsync();
            await renewTask;
        }
        if (Volatile.Read(ref lost) == 0)
        {
            if (Volatile.Read(ref cancelled) != 0) outcome = Communication.Enums.JobOutcome.Cancelled;
            using var commit = new CancellationTokenSource(options.LeaseDuration / 3);
            await store.Finish(lease, outcome, error, lease.Job.Policy.RetryDelay(lease.Job.Attempt, Random.Shared.NextDouble()), commit.Token);
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
                        await execution.CancelAsync();
                        return;
                    }
                    if (status == Communication.Enums.LeaseStatus.CancellationRequested)
                    {
                        Interlocked.Exchange(ref cancelled, 1);
                        await execution.CancelAsync();
                    }
                }
            }
            catch (OperationCanceledException) when (renewal.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref lost, 1);
                logger.LogWarning(ex, "Lease renewal failed for {JobId}", lease.Job.Id);
                await execution.CancelAsync();
            }
        }
    }
}
