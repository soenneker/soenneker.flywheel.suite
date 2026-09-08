using System.Collections.Concurrent;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Communication.Abstract;
using JobHistoryPoint = Soenneker.Flywheel.Core.Responses.JobHistoryPoint;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Logging.Dtos;
using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Responses;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard;

/// <summary>Per-connection snapshot subscriptions. Work is coalesced and triggered only by changes or explicit subscriptions.</summary>
public sealed partial class DashboardSubscriptions(
    IJobStore store,
    IHubContext<FlywheelHub, IFlywheelDashboardClient> hub,
    IHostApplicationLifetime lifetime,
    ILogger<DashboardSubscriptions> logger,
    IDashboardSnapshotFactory snapshots,
    IJobLogStore? logStore = null)
{
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();

    /// <summary>Replaces a connection's subscription and requests its initial snapshot.</summary>
    public async Task Subscribe(string connectionId, string kind, int version, string? query, int offset, int count,
        bool summary, string? jobId, CancellationToken disconnected, DateTimeOffset? startAt = null, DateTimeOffset? endAt = null)
    {
        if (version < 0 || offset < 0 || count is < 1 or > 200 || query?.Length > 200 || jobId?.Length > 200 ||
            startAt.HasValue != endAt.HasValue || startAt >= endAt)
            throw new HubException("Invalid subscription.");
        if (kind != "Board" && string.IsNullOrWhiteSpace(jobId))
            throw new HubException("Job ID is required.");
        if (startAt.HasValue && store is not IJobTimeRangeSearchStore)
            throw new HubException("The configured job store does not support time-range search.");
        await Remove(connectionId);
        var subscription = new Subscription(this, connectionId, kind, version, query, offset, count, summary, jobId, startAt, endAt,
            CancellationTokenSource.CreateLinkedTokenSource(disconnected, lifetime.ApplicationStopping));
        _subscriptions[connectionId] = subscription;
        subscription.Start();
    }

    /// <summary>Requests snapshots only for connections affected by the committed change.</summary>
    public void Changed(JobChange change)
    {
        foreach (Subscription subscription in _subscriptions.Values)
            if (change.Kind == "Resync" || subscription.Kind == "Board" && change.Kind is "Job" or "Schedules" or "Servers" ||
                subscription.Kind == "Job" && change.Kind == "Job" && subscription.JobId == change.JobId ||
                subscription.Kind == "Logs" && change.Kind == "Logs" && subscription.JobId == change.JobId)
                subscription.Signal();
    }

    /// <summary>Stops a disconnected connection's pending reads and deliveries.</summary>
    public async Task Remove(string connectionId)
    {
        if (_subscriptions.TryRemove(connectionId, out Subscription? subscription))
            await subscription.Stop();
    }

    private void LogFailure(Exception ex) => logger.LogWarning("Dashboard snapshot failed: {Error}", ex.GetType().Name);

    private async Task Send(string connectionId, string kind, int version, string? query, int offset, int count,
        bool summary, string? jobId, DateTimeOffset? startAt, DateTimeOffset? endAt, CancellationToken ct)
    {
        IFlywheelDashboardClient client = hub.Clients.Client(connectionId);
        if (kind == "Board")
        {
            JobSearchResult result = startAt is { } start && endAt is { } end && store is IJobTimeRangeSearchStore timeRangeStore
                ? await timeRangeStore.Search(query, start, end, offset, count, ct)
                : await store.Search(query, offset, count, ct);
            IReadOnlyList<JobHistoryPoint>? history = summary && store is IJobHistoryStore historyStore
                ? await historyStore.GetHistory(ct)
                : null;
            long? runningCount = store is IJobRunningCountStore runningStore
                ? await runningStore.GetRunningCount(ct)
                : null;
            int? totalWorkers = null;
            int? serverCount = null;
            if (store is IServerStore serverStore)
            {
                totalWorkers = await serverStore.GetTotalWorkerCount(ct);
                serverCount = (await serverStore.ListServers(200, ct)).Count;
            }
            ScheduleView? schedules = null;
            if (summary && store is IJobScheduleStore scheduleStore)
                schedules = snapshots.Schedules(await scheduleStore.ListRecurring(200, ct), await scheduleStore.ListScheduled(200, ct));
            await client.BoardSnapshot(new LiveBoard(version, result.Items.Select(snapshots.Job).ToList(),
                result.TotalCount, history is null ? null : snapshots.History(history), schedules, runningCount, serverCount, totalWorkers)).WaitAsync(ct);
        }
        else if (kind == "Job")
        {
            JobRecord? job = await store.Get(jobId!, ct);
            await client.JobSnapshot(new LiveJob(version, jobId!, job is null ? null : snapshots.Job(job))).WaitAsync(ct);
        }
        else if (kind == "Logs")
        {
            IJobLogStore? logs = logStore ?? store as IJobLogStore;
            IReadOnlyList<JobLogEntry>? entries = logs is not null ? await logs.GetLogs(jobId!, 200, ct) : null;
            await client.LogSnapshot(new LiveLogs(version, jobId!, entries is null ? null : snapshots.Logs(entries))).WaitAsync(ct);
        }
    }

}
