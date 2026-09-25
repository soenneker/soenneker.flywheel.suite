using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class MemoryJobStoreTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private static Communication.Requests.EnqueueRequest Request(string name = "job", TimeSpan delay = default,
        string? key = null, JobPolicy? policy = null) => new(name, "{}", policy ?? new JobPolicy(), delay, key);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public async Task StatusSearchPreservesCountsPagingAndQueuedBoundaries()
    {
        var clock = new Clock();
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        DateTimeOffset start = clock.GetUtcNow();
        for (int i = 0; i < 12; i++)
        {
            await store.Enqueue(Request("invoice", policy: new JobPolicy { MaxAttempts = 1 }));
            JobLease lease = (await store.Claim("node", TimeSpan.FromMinutes(1)))!;
            await store.Finish(lease, i % 2 == 0 ? JobOutcome.Failed : JobOutcome.Succeeded, null, TimeSpan.Zero);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        string queued = await store.Enqueue(Request("invoice"));
        string scheduled = await store.Enqueue(Request("invoice", TimeSpan.FromHours(1)));
        var excluded = new HashSet<string> { "Scheduled", "Queued", "Running", "Succeeded", "Cancelled", "Waiting" };
        JobSearchResult result = await Soenneker.Flywheel.Core.Dashboard.DashboardJobSearch.Search(store, "invoice", 2, 2, start, clock.GetUtcNow(), string.Join(',', excluded), default);
        Check(result.TotalCount == 6 && result.Items.Count == 2 && result.Items.All(j => j.State == JobState.DeadLettered), "Indexed status search lost counts or paging.");
        Check((await store.Search("missing", null, null, excluded)).TotalCount == 0, "Text filtering was ignored.");
        excluded.Add("DeadLettered");
        excluded.Remove("Queued");
        Check((await store.Search(null, null, null, excluded)).Items.Single().Id == queued, "Queued boundary was lost.");
        excluded.Add("Queued");
        excluded.Remove("Scheduled");
        Check((await store.Search(null, null, null, excluded)).Items.Single().Id == scheduled, "Scheduled boundary was lost.");
    }

    [Test]
    public async Task StartupSubmissionsAreOncePerInstance()
    {
        var store = new MemoryJobStore(new FlywheelMemoryOptions());
        string[] ids = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            store.EnqueueForCurrentInstance(Request("startup"), "v2", "current")));
        Check(ids.Distinct().Count() == 1, "Same instance submitted duplicate startup jobs.");
        Check(await store.ClaimForVersion("peer", TimeSpan.FromMinutes(1), "v2") is null, "Peer claimed another instance's job.");
        Check(await store.ClaimForVersion("current", TimeSpan.FromMinutes(1), "v1") is null, "Wrong version claimed startup job.");
        JobLease lease = (await store.ClaimForVersion("current", TimeSpan.FromMinutes(1), "v2"))!;
        Check(lease.Job.Id == ids[0] && lease.Job.TargetNodeId == "current", "Owner did not receive its job.");
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check(await store.EnqueueForCurrentInstance(Request("startup"), "v2", "current") == ids[0], "Completion allowed duplicate submission.");
        string restarted = await store.EnqueueForCurrentInstance(Request("startup"), "v2", "restarted");
        Check(restarted != ids[0], "New instance reused old startup job.");
        Check((await store.ClaimForVersion("restarted", TimeSpan.FromMinutes(1), "v2"))!.Job.Id == restarted, "Restarted instance could not run its job.");
    }

    [Test]
    public async Task StartupVersionRestrictionSurvivesRetries()
    {
        var store = new MemoryJobStore(new FlywheelMemoryOptions());
        string id = await store.EnqueueForCurrentInstance(Request("startup", policy: new JobPolicy { MaxAttempts = 2 }), "v2", "current");
        JobLease first = (await store.ClaimForVersion("current", TimeSpan.FromMinutes(1), "v2"))!;
        await store.Finish(first, JobOutcome.Failed, "transient", TimeSpan.Zero);
        Check(await store.ClaimForVersion("old", TimeSpan.FromMinutes(1), "v1") is null, "Old worker claimed the retry.");
        Check(await store.ClaimForVersion("peer", TimeSpan.FromMinutes(1), "v2") is null, "Peer claimed retry.");
        JobLease retry = (await store.ClaimForVersion("current", TimeSpan.FromMinutes(1), "v2"))!;
        Check(retry.Job.Id == id && retry.Job.Attempt == 2 && retry.Job.ApplicationVersion == "v2", "Normal version-restricted retries did not work.");
    }

    [Test]
    public async Task HeartbeatsRecordBoundedServerHistoryWithoutDashboardReads()
    {
        var clock = new Clock();
        var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        await store.Enqueue(Request());
        JobLease lease = (await store.Claim("one", TimeSpan.FromMinutes(10)))!;
        await store.Heartbeat("one", 8, TimeSpan.FromSeconds(30));
        await store.Heartbeat("two", 8, TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(10));
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero);
        await store.Heartbeat("one", 8, TimeSpan.FromSeconds(30));
        var server = (await store.GetServer("one"))!;
        Check(server.WorkerHistory.Select(point => point.BusyWorkers).SequenceEqual(new[] { 1, 0 }), "Heartbeats must record busy counts before any dashboard read.");
        Check((await store.GetServer("two"))!.WorkerHistory.Single().BusyWorkers == 0, "Server histories must remain separate.");
        for (int i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await store.Heartbeat("one", 8, TimeSpan.FromSeconds(30));
        }
        server = (await store.GetServer("one"))!;
        Check(server.WorkerHistory.Count <= 62 && server.WorkerHistory[^1].Timestamp == server.ObservedAt, "History must stay bounded and current.");
    }
    [Test]
    public async Task SearchPlacesRunningJobsBeforeNewerJobsAcrossPages()
    {
        var clock = new Clock();
        var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        string running = await store.Enqueue(Request("older"));
        await store.Claim("server", TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromSeconds(1));
        string newer = await store.Enqueue(Request("newer"));
        Check((await store.Search(null, 0, 1)).Items.Single().Id == running, "Running job must lead the first page.");
        Check((await store.Search(null, 1, 1)).Items.Single().Id == newer, "Newer non-running job must follow.");
        Check((await store.Search(null, clock.GetUtcNow().AddMinutes(-1), clock.GetUtcNow().AddMinutes(1), 0, 1)).Items.Single().Id == running,
            "Time-filtered searches must also put running jobs first.");
    }
    [Test]
    public async Task ConcurrentClaimsAndSubmissionsAreAtomic()
    {
        var store = new MemoryJobStore(new FlywheelMemoryOptions());
        string[] ids = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => store.Enqueue(Request(key: "same")))));
        Check(ids.Distinct().Count() == 1, "Concurrent deduplication failed.");
        JobLease?[] claims = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => store.Claim($"node-{i}", TimeSpan.FromMinutes(1)))));
        Check(claims.Count(c => c is not null) == 1, "A job was claimed by multiple workers.");
    }

    [Test]
    public async Task ExpiredLeasesCannotMutateAndRecoveryFencesOldOwners()
    {
        var clock = new Clock();
        var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        string id = await store.Enqueue(Request(policy: new JobPolicy { MaxAttempts = 2 }));
        JobLease first = (await store.Claim("first", TimeSpan.FromSeconds(1)))!;
        clock.Advance(TimeSpan.FromSeconds(1));
        Check(await store.Renew(first, TimeSpan.FromMinutes(1)) == LeaseStatus.Lost, "Expired renewal accepted.");
        Check(!await store.SetProgress(first, 50, null), "Expired progress accepted.");
        Check(!await store.AppendLogs(first, [new JobLogMessage("Info", "test", "stale")]), "Expired log accepted.");
        Check(!await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero), "Expired completion accepted.");
        await store.Maintain(100);
        Check(await store.Claim("second", TimeSpan.FromMinutes(1)) is null, "Recovery ignored retry backoff.");
        clock.Advance(TimeSpan.FromSeconds(5));
        JobLease second = (await store.Claim("second", TimeSpan.FromMinutes(1)))!;
        Check(second.Version > first.Version && second.Job.Attempt == 2, "Recovery did not fence the old lease.");
        Check(!await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero), "Old owner completed a new attempt.");
        Check(await store.Finish(second, JobOutcome.Succeeded, null, TimeSpan.Zero), "Current owner could not finish.");
        Check((await store.Get(id))!.State == JobState.Succeeded, "Completion missing.");
    }

    [Test]
    public async Task PriorityDelayVersionsAndLimitsControlDispatch()
    {
        var clock = new Clock();
        var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        await store.ConfigureMethod("limited", new MethodPolicy { MaxConcurrency = 1, RateLimit = 1, RateWindow = TimeSpan.FromMinutes(1) });
        string low = await store.Enqueue(Request("low", policy: new JobPolicy { Priority = JobPriority.Low }));
        string high = await store.Enqueue(Request("limited", policy: new JobPolicy { Priority = JobPriority.High }));
        await store.Enqueue(Request("limited", policy: new JobPolicy { Priority = JobPriority.High }));
        string delayed = await store.Enqueue(Request("delayed", TimeSpan.FromMinutes(2), policy: new JobPolicy { Priority = JobPriority.Critical }));
        string versioned = await store.EnqueueForCurrentInstance(Request("version", policy: new JobPolicy { Priority = JobPriority.Critical }), "v2", "node");
        JobLease first = (await store.Claim("node", TimeSpan.FromMinutes(5)))!;
        Check(first.Job.Id == high || first.Job.Name == "limited", "Priority was ignored.");
        Check((await store.ClaimForVersion("node", TimeSpan.FromMinutes(5), "v1"))!.Job.Id == low, "Limit or version filter was ignored.");
        Check((await store.ClaimForVersion("node", TimeSpan.FromMinutes(5), "v2"))!.Job.Id == versioned, "Matching version not claimed.");
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero);
        await store.ConfigureMethod("limited", new MethodPolicy { MaxConcurrency = 1, RateLimit = 1, RateWindow = TimeSpan.FromMinutes(1) });
        Check(await store.Claim("node", TimeSpan.FromMinutes(5)) is null, "Unchanged policy reset throttle usage.");
        clock.Advance(TimeSpan.FromMinutes(1));
        Check((await store.Claim("node", TimeSpan.FromMinutes(5)))!.Job.Name == "limited", "Rate window did not reset.");
        clock.Advance(TimeSpan.FromMinutes(1));
        Check((await store.Claim("node", TimeSpan.FromMinutes(5)))!.Job.Id == delayed, "Delayed job did not become eligible.");
    }

    [Test]
    public async Task ChainsReleaseAfterSuccessAndCancelSuffixOnFailure()
    {
        var clock = new Clock();
        var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        EnqueueRequest[] steps = new[] { Request("first"), Request("second", TimeSpan.FromSeconds(2), policy: new JobPolicy { MaxAttempts = 1 }), Request("third") };
        IReadOnlyList<string> ids = await store.EnqueueChain(steps, "chain");
        Check((await store.EnqueueChain(steps, "chain")).SequenceEqual(ids), "Chain deduplication failed.");
        JobLease first = (await store.Claim("node", TimeSpan.FromMinutes(1)))!;
        Check(first.Job.Id == ids[0] && await store.Claim("node", TimeSpan.FromMinutes(1)) is null, "Waiting step was claimed.");
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check(await store.Claim("node", TimeSpan.FromMinutes(1)) is null, "Successor delay ignored.");
        clock.Advance(TimeSpan.FromSeconds(2));
        JobLease second = (await store.Claim("node", TimeSpan.FromMinutes(1)))!;
        await store.Finish(second, JobOutcome.Failed, "failure", TimeSpan.Zero);
        Check((await store.Get(ids[1]))!.State == JobState.DeadLettered, "Attempt limit ignored.");
        Check((await store.Get(ids[2]))!.State == JobState.Cancelled, "Failure did not cancel suffix.");
    }

    [Test]
    public async Task DefaultPolicyDoesNotRetryFailures()
    {
        var store = new MemoryJobStore(new FlywheelMemoryOptions());
        string id = await store.Enqueue(Request());
        JobLease first = (await store.Claim("node", TimeSpan.FromMinutes(1)))!;
        await store.Finish(first, JobOutcome.Failed, "failure", TimeSpan.Zero);
        Check((await store.Get(id))!.State == JobState.DeadLettered, "Default policy retried a failed job.");
        Check(await store.Claim("node", TimeSpan.FromMinutes(1)) is null, "Failed job was available for retry.");
    }

    [Test]
    public async Task CancellationRetriesAndAttemptLimitsArePreserved()
    {
        var clock = new Clock();
        var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        string id = await store.Enqueue(Request(policy: new JobPolicy { MaxAttempts = 2 }));
        JobLease first = (await store.Claim("node", TimeSpan.FromMinutes(1)))!;
        await store.Finish(first, JobOutcome.Failed, "retry", TimeSpan.FromSeconds(2));
        Check(await store.Claim("node", TimeSpan.FromMinutes(1)) is null, "Retry delay ignored.");
        clock.Advance(TimeSpan.FromSeconds(2));
        JobLease second = (await store.Claim("node", TimeSpan.FromMinutes(1)))!;
        await store.Cancel(id);
        Check(await store.Renew(second, TimeSpan.FromMinutes(1)) == LeaseStatus.CancellationRequested, "Cancellation not communicated.");
        await store.Finish(second, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check((await store.Get(id))!.State == JobState.Cancelled, "Success overrode cancellation.");
        Check(!await store.Cancel(id), "Terminal job cancelled twice.");
        id = await store.Enqueue(Request(policy: new JobPolicy { MaxAttempts = 1 }));
        await store.Claim("node", TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.Maintain(100);
        Check((await store.Get(id))!.State == JobState.DeadLettered, "Lease recovery ignored attempt limit.");
    }

    [Test]
    public async Task SchedulesCoalesceAndManualRunsPreserveNextDueTime()
    {
        var clock = new Clock();
        var store = new MemoryJobStore(new FlywheelMemoryOptions { RetainCompletedJobs = false }, clock);
        Check(await store.AddRecurring("interval", Request(), TimeSpan.FromMinutes(1)), "Schedule not added.");
        Check(!await store.AddRecurring("interval", Request("changed"), TimeSpan.FromMinutes(2)), "Schedule replaced.");
        await store.Maintain(100);
        Check((await store.List()).Count == 1, "Initial occurrence missing.");
        clock.Advance(TimeSpan.FromMinutes(5));
        await store.Maintain(100);
        Check((await store.List()).Count == 2, "Missed ticks did not coalesce.");
        RecurringJobView view = (await store.ListRecurring()).Single();
        string manual = (await store.RunRecurring(view.Id))!;
        Check((await store.ListRecurring()).Single().DueAt == view.DueAt, "Manual run moved the schedule.");
        await store.Cancel(manual);
        await store.Maintain(100);
        Check((await store.ListRecurring()).Single().LastExecutionStatus == "Cancelled", "Pruning lost recurring status.");
        Check(await store.RunRecurring("missing") is null, "Missing schedule created a job.");
        await store.AddCron("cron", Request("cron"), "*/10 * * * * *", includeSeconds: true);
        clock.Advance(TimeSpan.FromSeconds(10));
        await store.Maintain(100);
        Check((await store.Search("cron")).TotalCount == 1, "Cron occurrence missing.");
        Check(await store.GetRecurringCount() == 2, "Incorrect schedule count.");
    }

    [Test]
    public async Task RetentionKeepsVersionMarkersAndHistoryButExpiresOrdinaryDedupe()
    {
        var clock = new Clock();
        var store = new MemoryJobStore(new FlywheelMemoryOptions { HistoryRetention = TimeSpan.FromMinutes(5) }, clock);
        string ordinary = await store.Enqueue(Request("ordinary", key: "key"));
        string version = await store.EnqueueForCurrentInstance(Request("version"), "v1", "node");
        for (int i = 0; i < 2; i++)
        {
            JobLease lease = (await store.ClaimForVersion("node", TimeSpan.FromMinutes(1), "v1"))!;
            await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero);
        }
        clock.Advance(TimeSpan.FromMinutes(4));
        await store.Maintain(100);
        Check((await store.List()).Count == 2, "Jobs pruned prematurely.");
        Check((await store.GetHistory()).Sum(p => p.Succeeded) == 2, "History transitions missing.");
        clock.Advance(TimeSpan.FromMinutes(2));
        await store.Maintain(100);
        Check((await store.List()).Count == 0, "Expired jobs retained.");
        Check(await store.EnqueueForCurrentInstance(Request("version"), "v1", "node") == version, "Version marker expired with job.");
        Check(await store.Enqueue(Request("ordinary", key: "key")) != ordinary, "Ordinary key did not expire.");
    }

    [Test]
    public async Task SearchDiagnosticsAndBoundedLogsUseCurrentState()
    {
        var clock = new Clock();
        var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        DateTimeOffset start = clock.GetUtcNow();
        string id = await store.Enqueue(Request("Needle"));
        await store.Enqueue(new EnqueueRequest("other", "{\"secret\":\"Needle\"}", new JobPolicy(), TimeSpan.FromMinutes(1)));
        JobLease lease = (await store.Claim("node", TimeSpan.FromMinutes(1)))!;
        await store.Heartbeat("node", 3, TimeSpan.FromSeconds(10));
        Check((await store.GetServer("node"))!.RunningJobs.Single().Id == id && await store.GetTotalWorkerCount() == 3, "Server diagnostic mismatch.");
        Check((await store.Search(" needle ")).TotalCount == 1, "Search matched payload or ignored trim.");
        Check((await store.Search(null, start, start.AddMilliseconds(1))).TotalCount == 2, "Range boundary mismatch.");
        Check((await store.GetSearchHistory("Needle", null, null)).Sum(p => p.Running) == 1, "Search history mismatch.");
        await store.SetProgress(lease, 42, "working");
        for (int i = 0; i < 21; i++)
            await store.AppendLogs(lease, Enumerable.Range(0, 50).Select(n => new JobLogMessage("Info", "test", $"{i * 50 + n}")).ToArray());
        IReadOnlyList<JobLogEntry> logs = await store.GetLogs(id);
        Check(logs.Count == 200 && logs[0].Message == "850" && logs[^1].Message == "1049", "Logs were not latest-first selection in chronological order.");
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check((await store.Get(id))!.Progress == 100 && await store.GetRunningCount() == 0, "Completion diagnostics mismatch.");
        clock.Advance(TimeSpan.FromSeconds(10));
        Check(await store.GetServer("node") is null && await store.GetTotalWorkerCount() == 0, "Expired server remained live.");
        Check((await store.GetLiveActivity()).Count == 61, "Live activity range mismatch.");
    }

    [Test]
    public async Task NotificationsBroadcastAndOverflowRequestsResync()
    {
        var store = new MemoryJobStore(new FlywheelMemoryOptions());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<JobChange> first = store.Watch(stop.Token).GetAsyncEnumerator();
        await using IAsyncEnumerator<JobChange> second = store.Watch(stop.Token).GetAsyncEnumerator();
        Check(await first.MoveNextAsync() && first.Current.Kind == "Resync", "Initial resync missing.");
        Check(await second.MoveNextAsync() && second.Current.Kind == "Resync", "Second subscriber not initialized.");
        string id = await store.Enqueue(Request());
        Check(await first.MoveNextAsync() && first.Current.JobId == id, "First subscriber missed change.");
        Check(await second.MoveNextAsync() && second.Current.JobId == id, "Notifications were consumed instead of broadcast.");
        for (int i = 0; i < 257; i++) await store.Enqueue(Request());
        Check(await first.MoveNextAsync() && first.Current.Kind == "Resync", "Overflow silently dropped changes.");
    }

    [Test]
    public async Task RegistrationSharesStoreWithinProviderAndIsolatesProviders()
    {
        var services = new ServiceCollection();
        services.AddSingleton<System.Text.Json.Serialization.JsonSerializerContext>(TestJsonContext.Default);
        services.AddLogging();
        services.AddFlywheel().AddMemory();
        await using ServiceProvider first = services.BuildServiceProvider();
        await using ServiceProvider second = services.BuildServiceProvider();
        var store = first.GetRequiredService<MemoryJobStore>();
        Check(ReferenceEquals(store, first.GetRequiredService<IJobStore>()) &&
              ReferenceEquals(store, first.GetRequiredService<IJobLogStore>()) &&
              ReferenceEquals(store, first.GetRequiredService<IJobChangeFeed>()), "Registration created separate stores.");
        string id = await store.Enqueue(Request());
        Check(await second.GetRequiredService<IJobStore>().Get(id) is null, "Separate providers share data.");
        IHostedService recorder = first.GetServices<IHostedService>().Single(s => s.GetType().Name == "MemoryLiveActivityRecorder");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await recorder.StartAsync(timeout.Token);
            while (!(await store.GetLiveActivity()).Any(p => p.QueuedCount == 1)) await Task.Delay(10, timeout.Token);
        }
        finally { await recorder.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task CancelledOperationsDoNotMutateStorage()
    {
        var store = new MemoryJobStore(new FlywheelMemoryOptions());
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        try { await store.Enqueue(Request(), stop.Token); throw new InvalidOperationException("Cancelled enqueue succeeded."); }
        catch (OperationCanceledException) { }
        Check((await store.List()).Count == 0, "Cancelled enqueue changed storage.");
        try { await store.EnqueueChain([Request(), Request(policy: new JobPolicy { MaxAttempts = 0 })]); throw new InvalidOperationException("Invalid chain succeeded."); }
        catch (ArgumentOutOfRangeException) { }
        Check((await store.List()).Count == 0, "Invalid chain was partially stored.");
    }
}

