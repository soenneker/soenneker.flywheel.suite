using System.Collections.Concurrent;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Communication.Abstract;
using JobHistoryPoint = Soenneker.Flywheel.Communication.Responses.JobHistoryPoint;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard;

/// <summary>Per-connection snapshot subscriptions. Work is coalesced and triggered only by changes or explicit subscriptions.</summary>
public sealed partial class DashboardSubscriptions(
    IJobStore store,
    IHubContext<FlywheelHub> hub,
    IHostApplicationLifetime lifetime,
    ILogger<DashboardSubscriptions> logger,
    IDashboardSnapshotFactory snapshots,
    IJobLogStore? logStore = null)
{
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();

    /// <summary>Replaces a connection's subscription and requests its initial snapshot.</summary>
    public async Task Subscribe(string connectionId, string kind, int version, string? query, int offset, int count,
        bool summary, string? jobId, CancellationToken disconnected, DateTimeOffset? startAt = null, DateTimeOffset? endAt = null, string? excludedStates = null)
    {
        if (!DashboardJobSearch.IsValid(excludedStates) || version < 0 || offset is < 0 or > 10000 || count is < 1 or > 200 || query?.Length > 200 || jobId?.Length > 200 ||
            startAt.HasValue != endAt.HasValue || startAt >= endAt)
            throw new HubException("Invalid subscription.");
        if (kind != "Board" && string.IsNullOrWhiteSpace(jobId))
            throw new HubException("Job ID is required.");
        if (startAt.HasValue && store is not IJobTimeRangeSearchStore)
            throw new HubException("The configured job store does not support time-range search.");
        await Remove(connectionId);
        var subscription = new Subscription(this, connectionId, kind, version, query, offset, count, summary, jobId, startAt, endAt, excludedStates,
            CancellationTokenSource.CreateLinkedTokenSource(disconnected, lifetime.ApplicationStopping));
        _subscriptions[connectionId] = subscription;
        subscription.Start();
    }

    /// <summary>Requests snapshots only for connections affected by the committed change.</summary>
    public void Changed(JobChange change)
    {
        if (change.Kind is "Job" or "Resync" or "Servers" or "Schedules")
            lock (_summaryGate) Array.Clear(_summaries);
        if (_subscriptions.IsEmpty) return;
        foreach (KeyValuePair<string, Subscription> entry in _subscriptions)
        {
            Subscription subscription = entry.Value;
            if (change.Kind == "Resync" || subscription.Kind == "Board" && change.Kind is "Job" or "Schedules" or "Servers" ||
                subscription.Kind == "Job" && change.Kind == "Job" && subscription.JobId == change.JobId ||
                subscription.Kind == "Logs" && change.Kind == "Logs" && subscription.JobId == change.JobId)
                subscription.Signal();
        }
    }

    /// <summary>Stops a disconnected connection's pending reads and deliveries.</summary>
    public async Task Remove(string connectionId)
    {
        if (_subscriptions.TryRemove(connectionId, out Subscription? subscription))
            await subscription.Stop();
    }

    private void LogFailure(Exception ex) => logger.LogWarning("Dashboard snapshot failed: {Error}", ex.GetType().Name);

    private async Task Send(string connectionId, string kind, int version, string? query, int offset, int count,
        bool summary, string? jobId, DateTimeOffset? startAt, DateTimeOffset? endAt, string? excludedStates, CancellationToken ct)
    {
        IClientProxy client = hub.Clients.Client(connectionId);
        if (kind == "Board")
        {
            JobSearchResult result = await DashboardJobSearch.Search(store, query, offset, count, startAt, endAt, excludedStates, ct);
            LiveBoard shared = await Summary(summary).WaitAsync(ct);
            await client.SendAsync(nameof(IFlywheelDashboardClient.BoardSnapshot), shared with
            {
                Version = version, Items = result.Items.Select(snapshots.Job).ToList(), TotalCount = result.TotalCount
            }, ct);
        }
        else if (kind == "Job")
        {
            JobRecord? job = await store.Get(jobId!, ct);
            await client.SendAsync(nameof(IFlywheelDashboardClient.JobSnapshot), new LiveJob(version, jobId!, job is null ? null : snapshots.Job(job)), ct);
        }
        else if (kind == "Logs")
        {
            IJobLogStore? logs = logStore ?? store as IJobLogStore;
            IReadOnlyList<JobLogEntry>? entries = logs is not null ? await logs.GetLogs(jobId!, 200, ct) : null;
            await client.SendAsync(nameof(IFlywheelDashboardClient.LogSnapshot), new LiveLogs(version, jobId!, entries is null ? null : snapshots.Logs(entries)), ct);
        }
    }

    private readonly Lock _summaryGate = new();
    private readonly Task<LiveBoard>?[] _summaries = new Task<LiveBoard>?[2];
    private readonly long[] _sampledAt = new long[2];

    private Task<LiveBoard> Summary(bool full)
    {
        int index = full ? 1 : 0;
        lock (_summaryGate)
        {
            var existing = _summaries[index];
            if (existing is not null && (!existing.IsCompleted || existing.IsCompletedSuccessfully &&
                System.Diagnostics.Stopwatch.GetElapsedTime(_sampledAt[index]) < TimeSpan.FromSeconds(1))) return existing;
            _sampledAt[index] = System.Diagnostics.Stopwatch.GetTimestamp();
            return _summaries[index] = ReadSummary(full);
        }
    }

    private async Task<LiveBoard> ReadSummary(bool summary)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        CancellationToken ct = timeout.Token;
        IReadOnlyList<JobHistoryPoint>? history = summary && store is IJobHistoryStore historyStore
            ? await historyStore.GetHistory(ct)
            : null;
        long? runningCount = store is IJobRunningCountStore runningStore
            ? await runningStore.GetRunningCount(ct)
            : null;
        long failedCount = store is IJobFailedCountStore failedStore
            ? await failedStore.GetFailedCount(ct)
            : (await DashboardJobSearch.Search(store, null, 0, 1, null, null,
                "Scheduled,Queued,Running,Succeeded,Cancelled,Waiting", ct)).TotalCount;
        long succeededCount = store is IJobSucceededCountStore succeededStore
            ? await succeededStore.GetSucceededCount(ct)
            : (await DashboardJobSearch.Search(store, null, 0, 1, null, null,
                "Scheduled,Queued,Running,DeadLettered,Cancelled,Waiting", ct)).TotalCount;
        int? totalWorkers = null;
        int? serverCount = null;
        if (store is IServerStore serverStore)
        {
            totalWorkers = await serverStore.GetTotalWorkerCount(ct);
            serverCount = await serverStore.GetServerCount(ct);
        }
        long? recurringCount = store is IRecurringJobCountStore recurringStore
            ? await recurringStore.GetRecurringCount(ct)
            : null;
        ScheduleView? schedules = null;
        IReadOnlyList<JobHistoryPoint>? liveActivity = summary && store is IJobLiveActivityStore liveStore
            ? await liveStore.GetLiveActivity(ct)
            : null;
        if (summary && store is IJobScheduleStore scheduleStore)
            schedules = await DashboardSchedules.Read(scheduleStore, snapshots, ct);
        return new LiveBoard(0, [], 0, history is null ? null : snapshots.History(history), schedules, runningCount,
            serverCount, totalWorkers, liveActivity?.ToList(), recurringCount, failedCount, succeededCount);
    }

}
