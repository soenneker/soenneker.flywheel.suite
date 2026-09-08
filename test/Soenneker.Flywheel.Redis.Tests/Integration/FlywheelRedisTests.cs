using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Logging.Dtos;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Enums;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Requests;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Flywheel.Core;
using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Responses;
using Soenneker.Flywheel.Generated;

namespace Soenneker.Flywheel.Redis.Tests;

// Avoid competing for Redis and thread-pool capacity across timing-sensitive tests.
// Distributed concurrency is exercised explicitly within each test.
[NotInParallel]
public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task CrossNodeFeedPublishesCommittedChangesAndResyncs() => WithStore(async (store, db, ns) =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var secondConnection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("FLYWHEEL_TEST_REDIS") ?? "localhost:16379");
        var observer = new RedisJobStore(_ => Task.FromResult(secondConnection.GetDatabase()), ns);
        await using var feed = observer.Watch(timeout.Token).GetAsyncEnumerator();
        Check(await feed.MoveNextAsync() && feed.Current.Kind == "Resync", "Subscription must start with a recovery snapshot trigger");
        var id = await store.Enqueue(Request());
        Check(await feed.MoveNextAsync() && feed.Current == new JobChange("Job", id), "Other node's enqueue was not delivered");
        Check((await observer.Get(id)) is not null, "Event was visible before the job was committed");
        var lease = (await store.Claim("worker", TimeSpan.FromSeconds(30)))!;
        Check(await feed.MoveNextAsync() && feed.Current.JobId == id, "Claim event missing");
        await store.AppendLogs(lease, [new("Information", "test", "pushed")]);
        Check(await feed.MoveNextAsync() && feed.Current.Kind == "Logs", "Log event missing");
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check(await feed.MoveNextAsync() && feed.Current.JobId == id, "Completion event missing");
        Check(!await store.Finish(lease, JobOutcome.Failed, null, TimeSpan.Zero), "Stale completion accepted");
        var next = feed.MoveNextAsync().AsTask();
        await Task.Delay(150);
        Check(!next.IsCompleted, "Idle or rejected mutation emitted a notification");
        await store.AddRecurring("schedule", Request(), TimeSpan.FromHours(1));
        Check(await next && feed.Current.Kind == "Schedules", "Schedule event missing");
    });

    [Test]
    public Task FeedResubscriptionCoversChangesWhileDisconnected() => WithStore(async (store, db, ns) =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observer = new RedisJobStore(_ => Task.FromResult(db), ns);
        await using (var first = observer.Watch(timeout.Token).GetAsyncEnumerator())
            Check(await first.MoveNextAsync() && first.Current.Kind == "Resync", "Initial sync missing");
        var id = await store.Enqueue(Request());
        await using var restored = observer.Watch(timeout.Token).GetAsyncEnumerator();
        Check(await restored.MoveNextAsync() && restored.Current.Kind == "Resync", "Resubscription did not request authoritative state");
        Check(await observer.Get(id) is not null, "Disconnected change missing from recovered snapshot");
    });

    [Test]
    public Task HistoryStillIncludesLegacyRecordsBeforeTransitionRecording() => WithStore(async (store, db, ns) =>
    {
        var tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        string prefix = $"flywheel:{{{tag}}}:v1:";
        long timestamp = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        var legacy = new JobRecord { Id = "legacy", Name = "legacy", Payload = "{}", Policy = new(),
            CreatedAt = timestamp, UpdatedAt = timestamp, DueAt = timestamp, State = JobState.Succeeded };
        await db.HashSetAsync(prefix + "jobs", legacy.Id, System.Text.Json.JsonSerializer.Serialize(legacy));
        await db.SortedSetAddAsync(prefix + "all", legacy.Id, timestamp);
        await store.Enqueue(Request());
        var history = await store.GetHistory();
        Check(history.Sum(p => p.Succeeded) == 1 && history.Sum(p => p.Scheduled) == 1, "Legacy history was dropped or double counted");
    });

    [Test]
    public Task DispatchFindsHighestPriorityAcrossBatchesAndBlockedFunctions() => WithStore(async store =>
    {
        await store.ConfigureMethod("blocked", new MethodPolicy { MaxConcurrency = 1 });
        await store.Enqueue(Request() with { Name = "blocked" });
        var running = (await store.Claim("occupy", TimeSpan.FromSeconds(30)))!;
        for (int i = 0; i < 260; i++) await store.Enqueue(Request() with
        {
            Name = "blocked", Policy = new JobPolicy { Priority = JobPriority.Critical },
            Payload = "{\"Policy\":{\"Priority\":999},\"Name\":\"decoy\"}"
        });
        await store.Enqueue(Request() with { Name = "available", Policy = new JobPolicy { Priority = JobPriority.Low } });
        var high = await store.Enqueue(Request() with { Name = "available", Policy = new JobPolicy { Priority = JobPriority.High } });
        var lease = (await store.Claim("available", TimeSpan.FromSeconds(30)))!;
        Check(lease.Job.Id == high, "Selection missed a later higher-priority job or blocked function prevented dispatch");
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero);
        await store.Finish(running, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check((await store.Claim("released", TimeSpan.FromSeconds(30)))!.Job.Name == "blocked", "Released function was not admitted");
    });

    [Test]
    public Task DispatchReadsLegacyPriorityAndEscapedNames() => WithStore(async (store, db, ns) =>
    {
        var low = await store.Enqueue(Request() with { Name = "other", Policy = new JobPolicy { Priority = JobPriority.Low } });
        var legacy = await store.Enqueue(Request() with { Name = "escaped\\name\"", Payload = "{\"Name\":\"decoy\",\"State\":1}" });
        var tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        RedisKey jobsKey = $"flywheel:{{{tag}}}:v1:jobs";
        var json = System.Text.Json.Nodes.JsonNode.Parse((string)(await db.HashGetAsync(jobsKey, legacy))!)!;
        json["Policy"]!.AsObject().Remove("Priority");
        await db.HashSetAsync(jobsKey, legacy, json.ToJsonString());
        var claim = (await store.Claim("legacy", TimeSpan.FromSeconds(30)))!;
        Check(claim.Job.Id == legacy && claim.Job.Policy.Priority == JobPriority.Normal, "Legacy default or escaped name parsing changed");
        Check((await store.Get(low))!.State == JobState.Scheduled, "Low priority executed ahead of legacy Normal");
    });

    [Test]
    public Task LogAppendsDoNotInvalidateDispatchSnapshots() => WithStore(async (store, db, ns) =>
    {
        await store.Enqueue(Request());
        var lease = (await store.Claim("logging", TimeSpan.FromSeconds(30)))!;
        var tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        RedisKey revisionKey = $"flywheel:{{{tag}}}:v1:revision";
        var before = await db.StringGetAsync(revisionKey);
        Check(await store.AppendLogs(lease, [new("Information", "test", "diagnostic")]), "Valid log append failed");
        Check(await db.StringGetAsync(revisionKey) == before, "Diagnostic write changed dispatch revision");
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Completion failed");
        Check(!await store.AppendLogs(lease, [new("Information", "test", "late")]), "Completed owner wrote logs");
    });

    [Test]
    public Task SeparateStoresShareThrottleAndSemaphoreCapacity() => WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 3, RateLimit = 2, RateWindow = TimeSpan.FromMinutes(1) });
        for (var i = 0; i < 12; i++) await store.Enqueue(Request());
        JobLease?[] claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? store : other).Claim("node" + i, TimeSpan.FromSeconds(30))));
        Check(claims.Count(c => c is not null) == 2, "Separate stores exceeded the shared throttle");
        foreach (JobLease? lease in claims.Where(c => c is not null)) await other.Finish(lease!, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check(await store.Claim("later", TimeSpan.FromSeconds(30)) is null, "Releasing permits reset rate usage");
    });

    [Test]
    public Task LoweringConcurrencyWaitsForExistingExecutions() => WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 4 });
        for (var i = 0; i < 5; i++) await store.Enqueue(Request());
        var leases = new JobLease[4];
        for (var i = 0; i < 4; i++) leases[i] = (await store.Claim("old", TimeSpan.FromSeconds(30)))!;
        await other.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 1 });
        for (var i = 0; i < 3; i++)
        {
            await other.Finish(leases[i], JobOutcome.Succeeded, null, TimeSpan.Zero);
            Check(await other.Claim("new", TimeSpan.FromSeconds(30)) is null, "Lower limit overlooked an existing permit");
        }
        await other.Finish(leases[3], JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check(await other.Claim("new", TimeSpan.FromSeconds(30)) is not null, "Drained function did not resume");
    });

    [Test]
    public Task SemaphorePermitRenewsWithJobAcrossStoreInstances() => WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 1 });
        await store.Enqueue(Request());
        await store.Enqueue(Request());
        JobLease first = (await store.Claim("first", TimeSpan.FromMilliseconds(300)))!;
        Check(await other.Renew(first, TimeSpan.FromSeconds(2)) == LeaseStatus.Renewed, "Renewal from another store failed");
        await Task.Delay(350);
        Check(await store.Claim("second", TimeSpan.FromSeconds(30)) is null, "Semaphore expired before renewed job lease");
        Check(await other.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero), "Other store could not release permit");
        Check(await store.Claim("second", TimeSpan.FromSeconds(30)) is not null, "Completion did not free semaphore");
    });

    [Test]
    public Task LostSemaphoreRejectsWritesWithoutDeletingSuccessor() => WithStore(async (store, db, ns) =>
    {
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 1 });
        await store.Enqueue(Request());
        JobLease lease = (await store.Claim("first", TimeSpan.FromSeconds(30)))!;
        string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        RedisValue json = await db.HashGetAsync($"flywheel:{{{tag}}}:v1:job-permits", lease.Job.Id);
        var permit = System.Text.Json.JsonSerializer.Deserialize<Soenneker.Redis.Semaphores.RedisSemaphorePermit>((string)json!)!;
        await db.StringSetAsync(permit.Key, "successor", TimeSpan.FromSeconds(30), false);
        Check(await store.Renew(lease, TimeSpan.FromSeconds(30)) == LeaseStatus.Lost, "Lost semaphore renewed job");
        Check(!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Lost semaphore committed outcome");
        Check(!await store.AppendLogs(lease, [new("Information", "test", "stale")]), "Lost semaphore appended logs");
        Check(await db.StringGetAsync(permit.Key) == "successor", "Old owner removed successor permit");
    });

    [Test]
    public Task PrioritiesRespectEligibilityAndRetryPolicy() => WithStore(async store =>
    {
        string low = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Low } });
        string normal = await store.Enqueue(Request());
        string high = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.High } });
        string critical = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Critical, MaxAttempts = 1 } });
        await store.Enqueue(Request() with { Delay = TimeSpan.FromHours(1), Policy = new JobPolicy { Priority = JobPriority.Critical } });
        foreach (string expected in new[] { critical, high, normal, low })
        {
            JobLease lease = (await store.Claim("priority", TimeSpan.FromSeconds(30)))!;
            Check(lease.Job.Id == expected, "Priority or due-time order incorrect");
            await store.Finish(lease, expected == critical ? JobOutcome.Failed : JobOutcome.Succeeded, null, TimeSpan.Zero);
        }
        Check((await store.Get(critical))!.State == JobState.DeadLettered, "Single-attempt policy retried");
        Check(await store.Claim("priority", TimeSpan.FromSeconds(30)) is null, "Future job ran early");
    });

    [Test]
    public Task FunctionConcurrencyIsAtomicAndDoesNotBlockOtherFunctions() => WithStore(async store =>
    {
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 2 });
        for (var i = 0; i < 8; i++) await store.Enqueue(Request());
        JobLease?[] claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => store.Claim("node" + i, TimeSpan.FromSeconds(30))));
        Check(claims.Count(x => x is not null) == 2, "Distributed concurrency exceeded");
        string other = await store.Enqueue(Request() with { Name = "other.v1", Policy = new JobPolicy { Priority = JobPriority.Low } });
        Check((await store.Claim("other", TimeSpan.FromSeconds(30)))!.Job.Id == other, "Blocked function starved other work");
        JobLease first = claims.First(x => x is not null)!;
        await store.Cancel(first.Job.Id);
        Check(await store.Claim("cancel", TimeSpan.FromSeconds(30)) is null, "Cancellation released a running lease early");
        await store.Finish(first, JobOutcome.Cancelled, null, TimeSpan.Zero);
        Check(await store.Claim("replacement", TimeSpan.FromSeconds(30)) is not null, "Completion did not release capacity");
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 3 });
        Check(await store.Claim("updated", TimeSpan.FromSeconds(30)) is not null, "Runtime policy did not affect queued jobs");
    });

    [Test]
    public Task ThrottleCountsRetriesAndPreservesUnchangedConfiguration() => WithStore(async store =>
    {
        var policy = new MethodPolicy { RateLimit = 1, RateWindow = TimeSpan.FromMilliseconds(400) };
        await store.ConfigureMethod("test.v1", policy);
        await store.Enqueue(Request());
        JobLease first = (await store.Claim("first", TimeSpan.FromSeconds(30)))!;
        await store.Finish(first, JobOutcome.Failed, null, TimeSpan.Zero);
        await store.ConfigureMethod("test.v1", policy);
        Check(await store.Claim("retry", TimeSpan.FromSeconds(30)) is null, "Retry bypassed rate limit or configuration reset usage");
        Check((await store.Get(first.Job.Id))!.Attempt == 1, "Throttling consumed an attempt");
        await Task.Delay(450);
        Check((await store.Claim("retry", TimeSpan.FromSeconds(30)))!.Job.Attempt == 2, "Window did not replenish");
        await store.Enqueue(Request());
        await store.ConfigureMethod("test.v1", new MethodPolicy());
        Check(await store.Claim("unlimited", TimeSpan.FromSeconds(30)) is not null, "Disabling throttling did not take effect");
    });

    [Test]
    public Task ExpiredLeaseReleasesFunctionCapacity() => WithStore(async store =>
    {
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 1 });
        await store.Enqueue(Request());
        await store.Enqueue(Request());
        JobLease expired = (await store.Claim("old", TimeSpan.FromMilliseconds(40)))!;
        await Task.Delay(80);
        Check(await store.Claim("new", TimeSpan.FromSeconds(30)) is not null, "Expired lease held capacity");
        Check(!await store.Finish(expired, JobOutcome.Succeeded, null, TimeSpan.Zero), "Expired owner committed");
    });

    [Test]
    public Task RuntimeClientSchedulesAndConfiguresRegisteredJobs() => WithStore(async store =>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlywheel().AddGeneratedJobs();
        services.AddSingleton<IJobStore>(store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IJobClient>();
        JobDefinition<TestPayload> job = FlywheelJobs.IntegrationJobs_Run;
        await client.ConfigureMethod(job, new MethodPolicy { MaxConcurrency = 1 });
        string future = await client.Schedule(job, new TestPayload("later"), DateTimeOffset.UtcNow.AddHours(1));
        Check(await store.Claim("early", TimeSpan.FromSeconds(30)) is null, "Scheduled job ran early");
        string immediate = await client.Schedule(job, new TestPayload("now"), DateTimeOffset.UtcNow.AddMinutes(-1));
        Check((await store.Claim("now", TimeSpan.FromSeconds(30)))!.Job.Id == immediate, "Past schedule was not immediately eligible");
        Check((await store.Get(future))!.State == JobState.Scheduled, "Future job was changed");
    });

    [Test]
    public Task ManualRecurringRunPreservesScheduleAndCreatesFreshExecution() => WithStore(async store =>
    {
        await store.AddRecurring("manual", Request() with { Payload = "{\"value\":42}" }, TimeSpan.FromHours(1));
        await store.Maintain(10);
        JobLease original = (await store.Claim("scheduled", TimeSpan.FromSeconds(30)))!;
        await store.Finish(original, JobOutcome.Succeeded, null, TimeSpan.Zero);
        RecurringJobView schedule = (await store.ListRecurring()).Single();
        string? id = await store.RunRecurring(schedule.Id);
        Check(id is not null && id != original.Job.Id, "Manual run did not create a separate job");
        JobRecord job = (await store.Get(id!))!;
        Check(job.State == JobState.Scheduled && job.Attempt == 0 && job.Version == 0 && !job.CancelRequested, "Manual run inherited execution state");
        Check(job.Payload == "{\"value\":42}" && job.Policy.MaxAttempts == 3, "Stored payload or policy lost");
        Check((await store.ListRecurring()).Single() == schedule, "Manual run changed the recurring schedule");
        Check((await store.Claim("manual", TimeSpan.FromSeconds(30)))!.Job.Id == id, "Manual execution is not immediately eligible");
        Check(await store.RunRecurring("missing") is null, "Missing schedule queued a job");
    });

    [Test]
    public Task HistoryRetainsTransitionsWithoutDashboardReads() => WithStore(async store =>
    {
        await store.Enqueue(Request());
        JobLease lease = (await store.Claim("history", TimeSpan.FromSeconds(30)))!;
        await store.Renew(lease, TimeSpan.FromSeconds(30));
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Completion failed");
        Check(!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Stale completion accepted");
        IReadOnlyList<JobHistoryPoint> history = await store.GetHistory();
        Check(history.Count == 289 && history.Zip(history.Skip(1)).All(pair => pair.Second.Timestamp - pair.First.Timestamp == 300000), "History buckets are not ordered and continuous");
        Check(history.Sum(point => point.Scheduled) == 1 && history.Sum(point => point.Running) == 1 && history.Sum(point => point.Succeeded) == 1,
            "Transitions were lost or lease renewal counted as activity");
        Check((await store.GetHistory()).Sum(point => point.Succeeded) == 1, "Reading history changed stored activity");
    });

    [Test]
    public Task DashboardSchedulesSeparateDefinitionsFromPendingExecutions() => WithStore(async store =>
    {
        await store.AddRecurring("hourly", Request() with { Name = "hourly.v1" }, TimeSpan.FromHours(1));
        string later = await store.Enqueue(Request() with { Delay = TimeSpan.FromHours(2) });
        string sooner = await store.Enqueue(Request() with { Delay = TimeSpan.FromHours(1) });
        RecurringJobView recurring = (await store.ListRecurring()).Single();
        Check(recurring.Name == "hourly.v1" && recurring.Interval == 3600000 && recurring.DueAt > 0, "Recurring metadata missing");
        Check((await store.ListScheduled(1)).Single().Id == sooner, "Pending jobs are not bounded and ordered by due time");
        await store.Maintain(10);
        Check((await store.ListRecurring()).Single().DueAt > recurring.DueAt, "Next recurring run did not advance");
        Check((await store.ListScheduled()).Count == 3, "Recurring occurrence is missing from pending executions");
        JobLease lease = (await store.Claim("dashboard", TimeSpan.FromSeconds(30)))!;
        Check((await store.ListScheduled()).All(job => job.Id != lease.Job.Id), "Running job still appears as scheduled");
        await store.Cancel(sooner);
        Check((await store.ListScheduled()).Single().Id == later, "Cancelled job still appears as scheduled");
    });

    [Test]
    public Task LogsAreFencedRetainedAndOrdered() => WithStore(async store =>
    {
        string id = await store.Enqueue(Request());
        JobLease lease = (await store.Claim("logs", TimeSpan.FromSeconds(30)))!;
        for (var batch = 0; batch < 21; batch++)
            Check(await store.AppendLogs(lease, Enumerable.Range(batch * 50, 50).Select(i => new JobLogMessage("Information", "test", i.ToString())).ToArray()), "Append rejected");
        IReadOnlyList<JobLogEntry> entries = await store.GetLogs(id);
        Check(entries.Count == 200 && entries[0].Message == "850" && entries[^1].Message == "1049", "Tail ordering incorrect");
        Check(entries.All(x => x.Attempt == 1 && x.Timestamp > 0), "Missing storage timestamp or attempt");
        JobLease forged = lease with { Token = "wrong" };
        Check(!await store.AppendLogs(forged, [new("Error", "test", "forged")]), "Stale owner appended logs");
        await store.Finish(lease, JobOutcome.Failed, "retry", TimeSpan.Zero);
        JobLease retry = (await store.Claim("retry", TimeSpan.FromSeconds(30)))!;
        Check(!await store.AppendLogs(lease, [new("Error", "test", "late")]), "Completed owner appended logs");
        Check(await store.AppendLogs(retry, [new("Information", "test", "second attempt")]), "Retry log rejected");
        IReadOnlyList<JobLogEntry> attempts = await store.GetLogs(id);
        Check(attempts.Any(x => x.Attempt == 1) && attempts[^1].Attempt == 2, "Retry erased history or attempt attribution");
        await store.Finish(retry, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check(!await store.AppendLogs(retry, [new("Error", "test", "after completion")]), "Terminal job accepted logs");
        Check((await store.GetLogs(id, 1)).Single().Message == "second attempt", "Logs lost after completion");
        await store.Enqueue(Request());
        JobLease expired = (await store.Claim("old", TimeSpan.FromMilliseconds(40)))!;
        await Task.Delay(80);
        Check(!await store.AppendLogs(expired, [new("Information", "test", "expired")]), "Expired lease wrote logs");
    });
    [Test]
    public Task SearchFindsJobsBeyondFirstPageAndPaginatesMatches() => WithStore(async store =>
    {
        string older = await store.Enqueue(Request() with { Name = "Invoices.Monthly.v1", Payload = "{\"secret\":\"payload-only\"}" });
        for (var i = 0; i < 120; i++) await store.Enqueue(Request() with { Name = "other.v1" });
        string newer = await store.Enqueue(Request() with { Name = "invoices.daily.v1" });
        JobSearchResult all = await store.Search("  INVOICES  ", count: 1);
        Check(all.TotalCount == 2 && all.Items.Single().Id == newer, "Search missed older jobs or returned the wrong first page");
        JobSearchResult next = await store.Search("invoices", offset: 1, count: 1);
        Check(next.TotalCount == 2 && next.Items.Single().Id == older, "Pagination applied before filtering");
        Check((await store.Search(older.ToUpperInvariant())).Items.Single().Id == older, "ID search failed");
        Check((await store.Search("scheduled")).TotalCount == 122, "State search failed");
        Check((await store.Search("payload-only")).TotalCount == 0, "Search included a private payload");
        Check((await store.Search(".*")).TotalCount == 0, "Search interpreted a pattern");
        Check((await store.Search("   ", count: 10)).TotalCount == 122, "Blank query should list all jobs");
        JobLease lease = (await store.Claim("Search-Worker", TimeSpan.FromSeconds(30)))!;
        Check((await store.Search("search-worker")).Items.Single().Id == lease.Job.Id, "Worker search failed");
        Check((await store.Search("invoices", offset: 50)).Items.Count == 0, "Out-of-range page should be empty");
        try { await store.Search(new string('x', 201)); throw new Exception("Oversized query accepted"); }
        catch (ArgumentOutOfRangeException) { }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await store.Search("invoices", cancellationToken: cancelled.Token); throw new Exception("Cancelled search accepted"); }
        catch (OperationCanceledException) { }
    });
    [Test]
    public Task GeneratedJobExecutesInScope() => WithStore(async store =>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlywheel().AddGeneratedJobs();
        services.AddSingleton<IJobStore>(store);
        services.AddSingleton<IJobLogStore>(store);
        services.AddSingleton<InvocationState>();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        string id = await provider.GetRequiredService<IJobClient>().Enqueue(FlywheelJobs.IntegrationJobs_Run, new TestPayload("delivered"));
        await provider.GetRequiredService<IJobExecutor>().RunOnce(CancellationToken.None);
        var state = provider.GetRequiredService<InvocationState>();
        Check(state.Value == "delivered" && state.Disposed, "Generated invocation or scope disposal failed");
        Check((await store.Get(id))!.State == JobState.Succeeded, "Execution not durable");
        Check((await store.GetLogs(id)).Any(x => x.Category == "Flywheel" && x.Message == "Handler finished: Succeeded."), "Runtime logs were not persisted");
        Check((await store.GetLogs(id)).Any(x => x.Message == "Delivered delivered"), "Handler ILogger output was not captured");
    });
    private static EnqueueRequest Request(string? key = null, int attempts = 3) => new("test.v1", "{}", new JobPolicy
    { MaxAttempts = attempts, InitialBackoff = TimeSpan.FromMilliseconds(1) }, TimeSpan.Zero, key);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Task WithStore(Func<RedisJobStore, Task> test) => WithStore((store, _, _) => test(store));

    private static async Task WithStore(Func<RedisJobStore, IDatabase, string, Task> test)
    {
        using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("FLYWHEEL_TEST_REDIS") ?? "localhost:16379,abortConnect=false,connectTimeout=2000");
        string ns = "tests-" + Guid.NewGuid().ToString("N");
        IDatabase db = connection.GetDatabase();
        var store = new RedisJobStore(_ => Task.FromResult(db), ns);
        try { await test(store, db, ns); }
        finally
        {
            string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
            IServer server = connection.GetServer((await db.IdentifyEndpointAsync($"flywheel:{{{tag}}}:v1:jobs"))!);
            await foreach (RedisKey key in server.KeysAsync(pattern: $"flywheel:{{{tag}}}:v1:*"))
                await db.KeyDeleteAsync(key);
        }
    }

    [Test]
    public Task ConcurrentEnqueueAndClaim() => WithStore(async store =>
    {
        string[] ids = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => store.Enqueue(Request("one"))));
        Check(ids.Distinct().Count() == 1, "Idempotent enqueue raced");
        JobLease?[] claims = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => store.Claim("node" + i, TimeSpan.FromSeconds(10))));
        Check(claims.Count(x => x != null) == 1, "More than one owner");
        JobLease lease = claims.Single(x => x != null)!;
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Completion failed");
        Check((await store.Get(ids[0]))!.State == JobState.Succeeded, "Success missing");
        Check(!await store.Finish(lease, JobOutcome.Failed, null, TimeSpan.Zero), "Old completion accepted");
    });

    [Test]
    public Task ExpiredOwnerCannotRenewOrCompleteAfterRecovery() => WithStore(async store =>
    {
        await store.Enqueue(Request());
        JobLease old = (await store.Claim("old", TimeSpan.FromMilliseconds(50)))!;
        await Task.Delay(100);
        Check(await store.Renew(old, TimeSpan.FromSeconds(10)) == LeaseStatus.Lost, "Expired lease resurrected");
        Check(!await store.Finish(old, JobOutcome.Succeeded, null, TimeSpan.Zero), "Expired completion accepted");
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.Maintain(100)));
        await Task.Delay(10);
        JobLease current = (await store.Claim("new", TimeSpan.FromSeconds(10)))!;
        Check(current.Version > old.Version && current.Token != old.Token && current.Job.Attempt == 2, "Fencing failed");
        Check(!await store.Finish(old, JobOutcome.Failed, null, TimeSpan.Zero), "Stale retry accepted");
        Check(await store.Renew(old, TimeSpan.FromSeconds(10)) == LeaseStatus.Lost, "Stale renewal accepted");
        Check(await store.Finish(current, JobOutcome.Succeeded, null, TimeSpan.Zero), "New owner failed");
    });

    [Test]
    public Task RetryDeadLetterAndCancellation() => WithStore(async (store, db, ns) =>
    {
        string id = await store.Enqueue(Request(attempts: 2));
        JobLease first = (await store.Claim("a", TimeSpan.FromSeconds(10)))!;
        TimeSpan retryDelay = TimeSpan.FromHours(1);
        Check(await store.Finish(first, JobOutcome.Failed, "test", retryDelay), "Retry was not persisted");
        JobRecord retry = (await store.Get(id))!;
        Check(retry.State == JobState.Scheduled && retry.DueAt - retry.UpdatedAt == (long)retryDelay.TotalMilliseconds,
            "Retry delay was not persisted");
        Check(await store.Claim("a", TimeSpan.FromSeconds(10)) is null, "Retry executed early");
        // Advance this isolated job to eligibility without depending on CI completing calls within 150 ms.
        string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        string prefix = $"flywheel:{{{tag}}}:v1:";
        await db.HashSetAsync(prefix + "jobs", id, System.Text.Json.JsonSerializer.Serialize(retry with { DueAt = 0 }));
        await db.SortedSetAddAsync(prefix + "due", id, 0);
        JobLease second = (await store.Claim("a", TimeSpan.FromSeconds(10)))!;
        Check(second.Job.Id == id && second.Job.Attempt == 2, "Wrong job or attempt retried");
        await store.Finish(second, JobOutcome.Failed, "test", TimeSpan.Zero);
        Check((await store.Get(id))!.State == JobState.DeadLettered, "Not dead lettered");
        string pending = await store.Enqueue(Request());
        await store.Cancel(pending);
        Check(await store.Claim("a", TimeSpan.FromSeconds(10)) is null, "Cancelled pending job claimed");
        string running = await store.Enqueue(Request());
        JobLease claim = (await store.Claim("a", TimeSpan.FromSeconds(10)))!;
        await store.Cancel(running);
        Check(await store.Renew(claim, TimeSpan.FromSeconds(10)) == LeaseStatus.CancellationRequested, "Cancellation not observed");
        await store.Finish(claim, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check((await store.Get(running))!.State == JobState.Cancelled, "Cancellation lost to completion");
    });

    [Test]
    public Task RecurrenceAndRecoveryAreBoundedAndAtomic() => WithStore(async store =>
    {
        bool[] results = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => store.AddRecurring("recurring", Request(), TimeSpan.FromHours(1))));
        Check(results.Count(x => x) == 1, "Duplicate schedules");
        await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => store.Maintain(100)));
        Check((await store.List()).Count == 1, "Duplicate occurrence");
        string id = await store.Enqueue(Request(attempts: 1));
        // Claim both eligible jobs and let both expire.
        await store.Claim("node", TimeSpan.FromMilliseconds(50));
        await store.Claim("node", TimeSpan.FromMilliseconds(50));
        await Task.Delay(100);
        await store.Maintain(100);
        Check((await store.Get(id))!.State == JobState.DeadLettered, "Recovery ignored attempt budget");
    });
}
