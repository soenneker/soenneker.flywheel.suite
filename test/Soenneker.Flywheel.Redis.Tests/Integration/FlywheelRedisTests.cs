using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Generated;

namespace Soenneker.Flywheel.Redis.Tests;

// Avoid competing for Redis and thread-pool capacity across timing-sensitive tests.
// Distributed concurrency is exercised explicitly within each test.
[NotInParallel]
public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask CrossNodeFeedPublishesCommittedChangesAndResyncs(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using ConnectionMultiplexer secondConnection =
            await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("FLYWHEEL_TEST_REDIS") ??
                                                     "localhost:6379");
        var observer = new RedisJobStore(_ => Task.FromResult(secondConnection.GetDatabase()), ns);
        await using IAsyncEnumerator<JobChange> feed = observer.Watch(timeout.Token).GetAsyncEnumerator(cancellationToken: cancellationToken);
        Check(await feed.MoveNextAsync() && feed.Current.Kind == "Resync",
            "Subscription must start with a recovery snapshot trigger");
        string id = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        Check(await feed.MoveNextAsync() && feed.Current == new JobChange("Job", id),
            "Other node's enqueue was not delivered");
        Check((await observer.Get(id, cancellationToken: cancellationToken)) is not null, "Event was visible before the job was committed");
        JobLease lease = (await store.Claim("worker", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check(await feed.MoveNextAsync() && feed.Current.JobId == id, "Claim event missing");
        await store.AppendLogs(lease, [new JobLogMessage("Information", "test", "pushed")], cancellationToken: cancellationToken);
        Check(await feed.MoveNextAsync() && feed.Current.Kind == "Logs", "Log event missing");
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check(await feed.MoveNextAsync() && feed.Current.JobId == id, "Completion event missing");
        Check(!await store.Finish(lease, JobOutcome.Failed, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Stale completion accepted");
        Task<bool> next = feed.MoveNextAsync().AsTask();
        await Task.Delay(150, cancellationToken: cancellationToken);
        Check(!next.IsCompleted, "Idle or rejected mutation emitted a notification");
        await store.AddRecurring("schedule", Request(), TimeSpan.FromHours(1), cancellationToken: cancellationToken);
        Check(await next && feed.Current.Kind == "Schedules", "Schedule event missing");
    }));

    [Test]
    public ValueTask FeedResubscriptionCoversChangesWhileDisconnected(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observer = new RedisJobStore(_ => Task.FromResult(db), ns);
        await using (IAsyncEnumerator<JobChange> first = observer.Watch(timeout.Token).GetAsyncEnumerator(cancellationToken: cancellationToken))
            Check(await first.MoveNextAsync() && first.Current.Kind == "Resync", "Initial sync missing");
        string id = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        await using IAsyncEnumerator<JobChange> restored = observer.Watch(timeout.Token).GetAsyncEnumerator(cancellationToken: cancellationToken);
        Check(await restored.MoveNextAsync() && restored.Current.Kind == "Resync",
            "Resubscription did not request authoritative state");
        Check(await observer.Get(id, cancellationToken: cancellationToken) is not null, "Disconnected change missing from recovered snapshot");
    }));

    [Test]
    public ValueTask DispatchFindsHighestPriorityAcrossBatchesAndBlockedFunctions(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        await store.ConfigureMethod("blocked", new MethodPolicy { MaxConcurrency = 1 }, cancellationToken: cancellationToken);
        await store.Enqueue(Request() with { Name = "blocked" }, cancellationToken: cancellationToken);
        JobLease running = (await store.Claim("occupy", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        for (int i = 0; i < 260; i++)
            await store.Enqueue(Request() with
            {
                Name = "blocked", Policy = new JobPolicy { Priority = JobPriority.Critical },
                Payload = "{\"Policy\":{\"Priority\":999},\"Name\":\"decoy\"}"
            }, cancellationToken: cancellationToken);
        await store.Enqueue(
            Request() with { Name = "available", Policy = new JobPolicy { Priority = JobPriority.Low } }, cancellationToken: cancellationToken);
        string high = await store.Enqueue(Request() with
        {
            Name = "available", Policy = new JobPolicy { Priority = JobPriority.High }
        }, cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("available", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check(lease.Job.Id == high,
            "Selection missed a later higher-priority job or blocked function prevented dispatch");
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        await store.Finish(running, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check((await store.Claim("released", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!.Job.Name == "blocked",
            "Released function was not admitted");
    }));

    [Test]
    public ValueTask DispatchReadsPriorityAndEscapedNames(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        string low = await store.Enqueue(Request() with
        {
            Name = "other", Policy = new JobPolicy { Priority = JobPriority.Low }
        }, cancellationToken: cancellationToken);
        string normal = await store.Enqueue(Request() with
        {
            Name = "escaped\\name\"", Payload = "{\"Name\":\"decoy\",\"State\":1}"
        }, cancellationToken: cancellationToken);
        JobLease claim = (await store.Claim("normal", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check(claim.Job.Id == normal && claim.Job.Policy.Priority == JobPriority.Normal,
            "Priority or escaped name parsing changed");
        Check((await store.Get(low, cancellationToken: cancellationToken))!.State == JobState.Scheduled, "Low priority executed ahead of Normal");
    }));

    [Test]
    public ValueTask LogAppendsDoNotInvalidateDispatchSnapshots(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("logging", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        string? before = await ControlValue(db, ns, "revision");
        Check(await store.AppendLogs(lease, [new JobLogMessage("Information", "test", "diagnostic")], cancellationToken: cancellationToken), "Valid log append failed");
        Check(await ControlValue(db, ns, "revision") == before, "Diagnostic write changed dispatch revision");
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Completion failed");
        Check(!await store.AppendLogs(lease, [new JobLogMessage("Information", "test", "late")], cancellationToken: cancellationToken), "Completed owner wrote logs");
    }));

    [Test]
    public ValueTask SeparateStoresShareThrottleAndSemaphoreCapacity(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.ConfigureMethod("test.v1",
            new MethodPolicy { MaxConcurrency = 3, RateLimit = 2, RateWindow = TimeSpan.FromMinutes(1) }, cancellationToken: cancellationToken);
        for (var i = 0; i < 12; i++)
            await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease?[] claims = await Task.WhenAll(Enumerable.Range(0, 12)
                                                          .Select(i =>
                                                              (i % 2 == 0 ? store : other).Claim("node" + i,
                                                                  TimeSpan.FromSeconds(30), cancellationToken: cancellationToken)));
        Check(claims.Count(c => c is not null) == 2, "Separate stores exceeded the shared throttle");
        foreach (JobLease? lease in claims.Where(c => c is not null))
            await other.Finish(lease!, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check(await store.Claim("later", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null, "Releasing permits reset rate usage");
    }));

    [Test]
    public ValueTask LoweringConcurrencyWaitsForExistingExecutions(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 4 }, cancellationToken: cancellationToken);
        for (var i = 0; i < 5; i++)
            await store.Enqueue(Request(), cancellationToken: cancellationToken);
        var leases = new JobLease[4];
        for (var i = 0; i < 4; i++)
            leases[i] = (await store.Claim("old", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        await other.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 1 }, cancellationToken: cancellationToken);
        for (var i = 0; i < 3; i++)
        {
            await other.Finish(leases[i], JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
            Check(await other.Claim("new", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null,
                "Lower limit overlooked an existing permit");
        }

        await other.Finish(leases[3], JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check(await other.Claim("new", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is not null, "Drained function did not resume");
    }));

    [Test]
    public ValueTask SemaphorePermitRenewsWithJobAcrossStoreInstances(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 1 }, cancellationToken: cancellationToken);
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease first = (await store.Claim("first", TimeSpan.FromMilliseconds(300), cancellationToken: cancellationToken))!;
        Check(await other.Renew(first, TimeSpan.FromSeconds(2), cancellationToken: cancellationToken) == LeaseStatus.Renewed,
            "Renewal from another store failed");
        await Task.Delay(350, cancellationToken: cancellationToken);
        Check(await store.Claim("second", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null,
            "Semaphore expired before renewed job lease");
        Check(await other.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken),
            "Other store could not release permit");
        Check(await store.Claim("second", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is not null, "Completion did not free semaphore");
    }));

    [Test]
    public ValueTask LostLeaseRejectsWritesWithoutDeletingSuccessor(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 1 }, cancellationToken: cancellationToken);
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("first", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        await SeedJob(db, ns, lease.Job with { Token = "successor", Version = lease.Version + 1 });
        Check(await store.Renew(lease, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) == LeaseStatus.Lost, "Lost owner renewed job");
        Check(!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Lost owner committed outcome");
        Check(!await store.AppendLogs(lease, [new JobLogMessage("Information", "test", "stale")], cancellationToken: cancellationToken), "Lost owner appended logs");
        Check((await store.Get(lease.Job.Id, cancellationToken: cancellationToken))!.Token == "successor", "Old owner removed successor lease");
    }));

    [Test]
    public ValueTask PrioritiesRespectEligibilityAndRetryPolicy(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        string low = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Low } }, cancellationToken: cancellationToken);
        string normal = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        string high = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.High } }, cancellationToken: cancellationToken);
        string critical = await store.Enqueue(Request() with
        {
            Policy = new JobPolicy { Priority = JobPriority.Critical, MaxAttempts = 1 }
        }, cancellationToken: cancellationToken);
        await store.Enqueue(Request() with
        {
            Delay = TimeSpan.FromHours(1), Policy = new JobPolicy { Priority = JobPriority.Critical }
        }, cancellationToken: cancellationToken);
        foreach (string expected in new[] { critical, high, normal, low })
        {
            JobLease lease = (await store.Claim("priority", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
            Check(lease.Job.Id == expected, "Priority or due-time order incorrect");
            await store.Finish(lease, expected == critical ? JobOutcome.Failed : JobOutcome.Succeeded, null,
                TimeSpan.Zero, cancellationToken: cancellationToken);
        }

        Check((await store.Get(critical, cancellationToken: cancellationToken))!.State == JobState.DeadLettered, "Single-attempt policy retried");
        Check(await store.Claim("priority", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null, "Future job ran early");
    }));

    [Test]
    public ValueTask FunctionConcurrencyIsAtomicAndDoesNotBlockOtherFunctions(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 2 }, cancellationToken: cancellationToken);
        for (var i = 0; i < 8; i++)
            await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease?[] claims =
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => store.Claim("node" + i, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken)));
        Check(claims.Count(x => x is not null) == 2, "Distributed concurrency exceeded");
        string other = await store.Enqueue(Request() with
        {
            Name = "other.v1", Policy = new JobPolicy { Priority = JobPriority.Low }
        }, cancellationToken: cancellationToken);
        Check((await store.Claim("other", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!.Job.Id == other,
            "Blocked function starved other work");
        JobLease first = claims.First(x => x is not null)!;
        await store.Cancel(first.Job.Id, cancellationToken: cancellationToken);
        Check(await store.Claim("cancel", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null,
            "Cancellation released a running lease early");
        await store.Finish(first, JobOutcome.Cancelled, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check(await store.Claim("replacement", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is not null,
            "Completion did not release capacity");
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 3 }, cancellationToken: cancellationToken);
        Check(await store.Claim("updated", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is not null,
            "Runtime policy did not affect queued jobs");
    }));

    [Test]
    public ValueTask ThrottleCountsRetriesAndPreservesUnchangedConfiguration(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        var policy = new MethodPolicy { RateLimit = 1, RateWindow = TimeSpan.FromMilliseconds(400) };
        await store.ConfigureMethod("test.v1", policy, cancellationToken: cancellationToken);
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease first = (await store.Claim("first", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        await store.Finish(first, JobOutcome.Failed, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        await store.ConfigureMethod("test.v1", policy, cancellationToken: cancellationToken);
        Check(await store.Claim("retry", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null,
            "Retry bypassed rate limit or configuration reset usage");
        Check((await store.Get(first.Job.Id, cancellationToken: cancellationToken))!.Attempt == 1, "Throttling consumed an attempt");
        await Task.Delay(450, cancellationToken: cancellationToken);
        Check((await store.Claim("retry", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!.Job.Attempt == 2, "Window did not replenish");
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        await store.ConfigureMethod("test.v1", new MethodPolicy(), cancellationToken: cancellationToken);
        Check(await store.Claim("unlimited", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is not null,
            "Disabling throttling did not take effect");
    }));

    [Test]
    public ValueTask ExpiredLeaseReleasesFunctionCapacity(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 1 }, cancellationToken: cancellationToken);
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease expired = (await store.Claim("old", TimeSpan.FromMilliseconds(40), cancellationToken: cancellationToken))!;
        await Task.Delay(80, cancellationToken: cancellationToken);
        Check(await store.Claim("new", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is not null, "Expired lease held capacity");
        Check(!await store.Finish(expired, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Expired owner committed");
    }));

    [Test]
    public ValueTask RuntimeClientSchedulesAndConfiguresRegisteredJobs(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        var services = new ServiceCollection();
        services.AddSingleton<System.Text.Json.Serialization.JsonSerializerContext>(TestJsonContext.Default);
        services.AddLogging();
        services.AddFlywheel().AddGeneratedJobs();
        services.AddSingleton<IJobStore>(store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IJobClient>();
        JobDefinition<TestPayload> job = FlywheelJobs.IntegrationJobs_Run;
        await client.ConfigureMethod(job, new MethodPolicy { MaxConcurrency = 1 }, cancellationToken: cancellationToken);
        string future = await client.Schedule(job, new TestPayload("later"), DateTimeOffset.UtcNow.AddHours(1), cancellationToken: cancellationToken);
        Check(await store.Claim("early", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null, "Scheduled job ran early");
        string immediate = await client.Schedule(job, new TestPayload("now"), DateTimeOffset.UtcNow.AddMinutes(-1), cancellationToken: cancellationToken);
        Check((await store.Claim("now", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!.Job.Id == immediate,
            "Past schedule was not immediately eligible");
        Check((await store.Get(future, cancellationToken: cancellationToken))!.State == JobState.Scheduled, "Future job was changed");
    }));

    [Test]
    public ValueTask ManualRecurringRunPreservesScheduleAndCreatesFreshExecution(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        await store.AddRecurring("manual", Request() with { Payload = "{\"value\":42}" }, TimeSpan.FromHours(1), cancellationToken: cancellationToken);
        await store.Maintain(10, cancellationToken: cancellationToken);
        JobLease original = (await store.Claim("scheduled", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        await store.Finish(original, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        RecurringJobView schedule = (await store.ListRecurring(cancellationToken: cancellationToken)).Single();
        string? id = await store.RunRecurring(schedule.Id, cancellationToken: cancellationToken);
        Check(id is not null && id != original.Job.Id, "Manual run did not create a separate job");
        JobRecord job = (await store.Get(id!, cancellationToken: cancellationToken))!;
        Check(job.State == JobState.Scheduled && job.Attempt == 0 && job.Version == 0 && !job.CancelRequested,
            "Manual run inherited execution state");
        Check(job.Payload == "{\"value\":42}" && job.Policy.MaxAttempts == 3, "Stored payload or policy lost");
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Single() == schedule with { LastExecutionStatus = "Queued", LastExecutionId = id },
            "Manual run changed the recurring schedule metadata");
        Check((await store.Claim("manual", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!.Job.Id == id,
            "Manual execution is not immediately eligible");
        Check(await store.RunRecurring("missing", cancellationToken: cancellationToken) is null, "Missing schedule queued a job");
    }));

    [Test]
    public ValueTask HistoryRetainsTransitionsWithoutDashboardReads(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("history", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        await store.Renew(lease, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Completion failed");
        Check(!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Stale completion accepted");
        IReadOnlyList<JobHistoryPoint> history = await store.GetHistory(cancellationToken: cancellationToken);
        Check(
            history.Count == 289 && history.Zip(history.Skip(1))
                                           .All(pair => pair.Second.Timestamp - pair.First.Timestamp == 300000),
            "History buckets are not ordered and continuous");
        Check(
            history.Sum(point => point.Scheduled) == 1 && history.Sum(point => point.Running) == 1 &&
            history.Sum(point => point.Succeeded) == 1, "Transitions were lost or lease renewal counted as activity");
        Check((await store.GetHistory(cancellationToken: cancellationToken)).Sum(point => point.Succeeded) == 1, "Reading history changed stored activity");
    }));

    [Test]
    public ValueTask DashboardSchedulesSeparateDefinitionsFromPendingExecutions(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        await store.AddRecurring("hourly", Request() with { Name = "hourly.v1" }, TimeSpan.FromHours(1), cancellationToken: cancellationToken);
        string later = await store.Enqueue(Request() with { Delay = TimeSpan.FromHours(2) }, cancellationToken: cancellationToken);
        string sooner = await store.Enqueue(Request() with { Delay = TimeSpan.FromHours(1) }, cancellationToken: cancellationToken);
        RecurringJobView recurring = (await store.ListRecurring(cancellationToken: cancellationToken)).Single();
        Check(recurring.Name == "hourly.v1" && recurring.Interval == 3600000 && recurring.DueAt > 0,
            "Recurring metadata missing");
        Check((await store.ListScheduled(1, cancellationToken: cancellationToken)).Single().Id == sooner,
            "Pending jobs are not bounded and ordered by due time");
        await store.Maintain(10, cancellationToken: cancellationToken);
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Single().DueAt > recurring.DueAt, "Next recurring run did not advance");
        Check((await store.ListScheduled(cancellationToken: cancellationToken)).Count == 3, "Recurring occurrence is missing from pending executions");
        JobLease lease = (await store.Claim("dashboard", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check((await store.ListScheduled(cancellationToken: cancellationToken)).All(job => job.Id != lease.Job.Id),
            "Running job still appears as scheduled");
        await store.Cancel(sooner, cancellationToken: cancellationToken);
        Check((await store.ListScheduled(cancellationToken: cancellationToken)).Single().Id == later, "Cancelled job still appears as scheduled");
    }));

    [Test]
    public ValueTask LogsAreFencedRetainedAndOrdered(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        string id = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("logs", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        for (var batch = 0; batch < 21; batch++)
            Check(
                await store.AppendLogs(lease,
                    Enumerable.Range(batch * 50, 50).Select(i => new JobLogMessage("Information", "test", i.ToString()))
                              .ToArray(), cancellationToken: cancellationToken), "Append rejected");
        IReadOnlyList<JobLogEntry> entries = await store.GetLogs(id, cancellationToken: cancellationToken);
        Check(entries.Count == 200 && entries[0].Message == "850" && entries[^1].Message == "1049",
            "Tail ordering incorrect");
        Check(entries.All(x => x.Attempt == 1 && x.Timestamp > 0), "Missing storage timestamp or attempt");
        JobLease forged = lease with { Token = "wrong" };
        Check(!await store.AppendLogs(forged, [new JobLogMessage("Error", "test", "forged")], cancellationToken: cancellationToken), "Stale owner appended logs");
        await store.Finish(lease, JobOutcome.Failed, "retry", TimeSpan.Zero, cancellationToken: cancellationToken);
        JobLease retry = (await store.Claim("retry", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check(!await store.AppendLogs(lease, [new JobLogMessage("Error", "test", "late")], cancellationToken: cancellationToken), "Completed owner appended logs");
        Check(await store.AppendLogs(retry, [new JobLogMessage("Information", "test", "second attempt")], cancellationToken: cancellationToken), "Retry log rejected");
        IReadOnlyList<JobLogEntry> attempts = await store.GetLogs(id, cancellationToken: cancellationToken);
        Check(attempts.Any(x => x.Attempt == 1) && attempts[^1].Attempt == 2,
            "Retry erased history or attempt attribution");
        await store.Finish(retry, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check(!await store.AppendLogs(retry, [new JobLogMessage("Error", "test", "after completion")], cancellationToken: cancellationToken), "Terminal job accepted logs");
        Check((await store.GetLogs(id, 1, cancellationToken: cancellationToken)).Single().Message == "second attempt", "Logs lost after completion");
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease expired = (await store.Claim("old", TimeSpan.FromMilliseconds(40), cancellationToken: cancellationToken))!;
        await Task.Delay(80, cancellationToken: cancellationToken);
        Check(!await store.AppendLogs(expired, [new JobLogMessage("Information", "test", "expired")], cancellationToken: cancellationToken), "Expired lease wrote logs");
    }));

    [Test]
    public ValueTask SearchFindsJobsBeyondFirstPageAndPaginatesMatches(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        string older = await store.Enqueue(Request() with
        {
            Name = "Invoices.Monthly.v1", Payload = "{\"secret\":\"payload-only\"}"
        }, cancellationToken: cancellationToken);
        for (var i = 0; i < 120; i++)
            await store.Enqueue(Request() with { Name = "other.v1" }, cancellationToken: cancellationToken);
        string newer = await store.Enqueue(Request() with { Name = "invoices.daily.v1" }, cancellationToken: cancellationToken);
        JobSearchResult all = await store.Search("  INVOICES  ", count: 1, cancellationToken: cancellationToken);
        Check(all.TotalCount == 2 && all.Items.Single().Id == newer,
            "Search missed older jobs or returned the wrong first page");
        JobSearchResult next = await store.Search("invoices", offset: 1, count: 1, cancellationToken: cancellationToken);
        Check(next.TotalCount == 2 && next.Items.Single().Id == older, "Pagination applied before filtering");
        Check((await store.Search(older.ToUpperInvariant(), cancellationToken: cancellationToken)).Items.Single().Id == older, "ID search failed");
        JobSearchResult queued = await store.Search("queued", cancellationToken: cancellationToken);
        Check(queued.TotalCount == 122, $"Queued state search found {queued.TotalCount} of 122 jobs");
        DateTimeOffset start = DateTimeOffset.FromUnixTimeMilliseconds((await store.Get(older, cancellationToken: cancellationToken))!.CreatedAt);
        Check((await store.Search("queued", start, start.AddHours(1), cancellationToken: cancellationToken)).TotalCount == 122,
            "Date-bounded search omitted queued jobs");
        Check((await store.GetSearchHistory("queued", null, null, cancellationToken: cancellationToken)).Sum(point => point.Queued) == 122,
            "Search history omitted queued jobs");
        Check((await store.Search("scheduled", cancellationToken: cancellationToken)).TotalCount == 0, "Scheduled search included queued jobs");
        Check((await store.Search("payload-only", cancellationToken: cancellationToken)).TotalCount == 0, "Search included a private payload");
        Check((await store.Search(".*", cancellationToken: cancellationToken)).TotalCount == 0, "Search interpreted a pattern");
        Check((await store.Search("   ", count: 10, cancellationToken: cancellationToken)).TotalCount == 122, "Blank query should list all jobs");
        string scheduled = await store.Enqueue(Request() with { Delay = TimeSpan.FromHours(1) }, cancellationToken: cancellationToken);
        JobSearchResult scheduledJobs = await store.Search("scheduled", cancellationToken: cancellationToken);
        Check(scheduledJobs.TotalCount == 1 && scheduledJobs.Items.Single().Id == scheduled,
            "Scheduled state search failed");
        Check((await store.Search("queued", cancellationToken: cancellationToken)).TotalCount == 122, "Queued search included future scheduled jobs");
        IReadOnlyList<JobHistoryPoint> pendingHistory = await store.GetSearchHistory(null, null, null, cancellationToken: cancellationToken);
        Check(pendingHistory.Sum(point => point.Queued) == 122 && pendingHistory.Sum(point => point.Scheduled) == 1,
            "Search history did not distinguish queued and future scheduled jobs");
        JobLease lease = (await store.Claim("Search-Worker", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check((await store.Search("search-worker", cancellationToken: cancellationToken)).Items.Single().Id == lease.Job.Id, "Worker search failed");
        Check((await store.Search("invoices", offset: 50, cancellationToken: cancellationToken)).Items.Count == 0, "Out-of-range page should be empty");
        try
        {
            await store.Search(new string('x', 201), cancellationToken: cancellationToken);
            throw new Exception("Oversized query accepted");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        try
        {
            await store.Search("invoices", cancellationToken: cancelled.Token);
            throw new Exception("Cancelled search accepted");
        }
        catch (OperationCanceledException)
        {
        }
    }));

    [Test]
    public ValueTask GeneratedJobExecutesInScope(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        var services = new ServiceCollection();
        services.AddSingleton<System.Text.Json.Serialization.JsonSerializerContext>(TestJsonContext.Default);
        services.AddLogging();
        services.AddFlywheel().AddGeneratedJobs();
        services.AddSingleton<IJobStore>(store);
        services.AddSingleton<IJobLogStore>(store);
        services.AddSingleton<InvocationState>();
        await using ServiceProvider provider =
            services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        string id = await provider.GetRequiredService<IJobClient>()
                                  .Enqueue(FlywheelJobs.IntegrationJobs_Run, new TestPayload("delivered"), cancellationToken: cancellationToken);
        await provider.GetRequiredService<IJobExecutor>().RunOnce(cancellationToken);
        var state = provider.GetRequiredService<InvocationState>();
        Check(state.Value == "delivered" && state.Disposed, "Generated invocation or scope disposal failed");
        Check((await store.Get(id, cancellationToken: cancellationToken))!.State == JobState.Succeeded, "Execution not durable");
        Check(
            (await store.GetLogs(id, cancellationToken: cancellationToken)).Any(x => x.Category == "Flywheel" && x.Message == "Handler finished: Succeeded."),
            "Runtime logs were not persisted");
        Check((await store.GetLogs(id, cancellationToken: cancellationToken)).Any(x => x.Message == "Delivered delivered"),
            "Handler ILogger output was not captured");
    }));

    private static EnqueueRequest Request(string? key = null, int attempts = 3) => new("test.v1", "{}",
        new JobPolicy { MaxAttempts = attempts, InitialBackoff = TimeSpan.FromMilliseconds(1) }, TimeSpan.Zero, key);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static Task WithStore(Func<RedisJobStore, Task> test) => WithStore((store, _, _) => test(store));

    private static async Task WithStore(Func<RedisJobStore, IDatabase, string, Task> test)
    {
        using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable("FLYWHEEL_TEST_REDIS") ??
            "localhost:6379,abortConnect=false,connectTimeout=2000");
        string ns = "tests-" + Guid.NewGuid().ToString("N");
        IDatabase db = connection.GetDatabase();
        var store = new RedisJobStore(_ => Task.FromResult(db), ns);
        try
        {
            await test(store, db, ns);
        }
        finally
        {
            await store.DisposeAsync();
            string prefix = LibrarianPrefix(ns);
            IServer server = connection.GetServer((await db.IdentifyEndpointAsync(prefix + "clock"))!);
            await foreach (RedisKey key in server.KeysAsync(pattern: prefix + "*")) await db.KeyDeleteAsync(key);
        }
    }

    [Test]
    public ValueTask ConcurrentEnqueueAndClaim(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        string[] ids = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => store.Enqueue(Request("one"), cancellationToken: cancellationToken)));
        Check(ids.Distinct().Count() == 1, "Idempotent enqueue raced");
        JobLease?[] claims =
            await Task.WhenAll(Enumerable.Range(0, 30).Select(i => store.Claim("node" + i, TimeSpan.FromSeconds(10), cancellationToken: cancellationToken)));
        Check(claims.Count(x => x != null) == 1, "More than one owner");
        JobLease lease = claims.Single(x => x != null)!;
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Completion failed");
        Check((await store.Get(ids[0], cancellationToken: cancellationToken))!.State == JobState.Succeeded, "Success missing");
        Check(!await store.Finish(lease, JobOutcome.Failed, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Old completion accepted");
    }));

    [Test]
    public ValueTask ExpiredOwnerCannotRenewOrCompleteAfterRecovery(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease old = (await store.Claim("old", TimeSpan.FromMilliseconds(50), cancellationToken: cancellationToken))!;
        await Task.Delay(100, cancellationToken: cancellationToken);
        Check(await store.Renew(old, TimeSpan.FromSeconds(10), cancellationToken: cancellationToken) == LeaseStatus.Lost, "Expired lease resurrected");
        Check(!await store.Finish(old, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Expired completion accepted");
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => store.Maintain(100, cancellationToken: cancellationToken)));
        await Task.Delay(10, cancellationToken: cancellationToken);
        JobLease current = (await store.Claim("new", TimeSpan.FromSeconds(10), cancellationToken: cancellationToken))!;
        Check(current.Version > old.Version && current.Token != old.Token && current.Job.Attempt == 2,
            "Fencing failed");
        Check(!await store.Finish(old, JobOutcome.Failed, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Stale retry accepted");
        Check(await store.Renew(old, TimeSpan.FromSeconds(10), cancellationToken: cancellationToken) == LeaseStatus.Lost, "Stale renewal accepted");
        Check(await store.Finish(current, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "New owner failed");
    }));

    [Test]
    public ValueTask RetryDeadLetterAndCancellation(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        string id = await store.Enqueue(Request(attempts: 2), cancellationToken: cancellationToken);
        JobLease first = (await store.Claim("a", TimeSpan.FromSeconds(10), cancellationToken: cancellationToken))!;
        TimeSpan retryDelay = TimeSpan.FromHours(1);
        Check(await store.Finish(first, JobOutcome.Failed, "test", retryDelay, cancellationToken: cancellationToken), "Retry was not persisted");
        JobRecord retry = (await store.Get(id, cancellationToken: cancellationToken))!;
        Check(retry.State == JobState.Scheduled && retry.DueAt - retry.UpdatedAt == (long)retryDelay.TotalMilliseconds,
            "Retry delay was not persisted");
        Check(await store.Claim("a", TimeSpan.FromSeconds(10), cancellationToken: cancellationToken) is null, "Retry executed early");
        // Advance this isolated job to eligibility without depending on CI completing calls within 150 ms.
        await SeedJob(db, ns, retry with { DueAt = 0 });
        JobLease second = (await store.Claim("a", TimeSpan.FromSeconds(10), cancellationToken: cancellationToken))!;
        Check(second.Job.Id == id && second.Job.Attempt == 2, "Wrong job or attempt retried");
        await store.Finish(second, JobOutcome.Failed, "test", TimeSpan.Zero, cancellationToken: cancellationToken);
        Check((await store.Get(id, cancellationToken: cancellationToken))!.State == JobState.DeadLettered, "Not dead lettered");
        string pending = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        await store.Cancel(pending, cancellationToken: cancellationToken);
        Check(await store.Claim("a", TimeSpan.FromSeconds(10), cancellationToken: cancellationToken) is null, "Cancelled pending job claimed");
        string running = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease claim = (await store.Claim("a", TimeSpan.FromSeconds(10), cancellationToken: cancellationToken))!;
        await store.Cancel(running, cancellationToken: cancellationToken);
        Check(await store.Renew(claim, TimeSpan.FromSeconds(10), cancellationToken: cancellationToken) == LeaseStatus.CancellationRequested,
            "Cancellation not observed");
        await store.Finish(claim, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check((await store.Get(running, cancellationToken: cancellationToken))!.State == JobState.Cancelled, "Cancellation lost to completion");
    }));

    [Test]
    public ValueTask RecurrenceAndRecoveryAreBoundedAndAtomic(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        bool[] results = await Task.WhenAll(Enumerable.Range(0, 15)
                                                      .Select(_ => store.AddRecurring("recurring", Request(),
                                                          TimeSpan.FromHours(1), cancellationToken: cancellationToken)));
        Check(results.Count(x => x) == 1, "Duplicate schedules");
        await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => store.Maintain(100, cancellationToken: cancellationToken)));
        Check((await store.List(cancellationToken: cancellationToken)).Count == 1, "Duplicate occurrence");
        string id = await store.Enqueue(Request(attempts: 1), cancellationToken: cancellationToken);
        // Claim both eligible jobs and let both expire.
        await store.Claim("node", TimeSpan.FromMilliseconds(50), cancellationToken: cancellationToken);
        await store.Claim("node", TimeSpan.FromMilliseconds(50), cancellationToken: cancellationToken);
        await Task.Delay(100, cancellationToken: cancellationToken);
        await store.Maintain(100, cancellationToken: cancellationToken);
        Check((await store.Get(id, cancellationToken: cancellationToken))!.State == JobState.DeadLettered, "Recovery ignored attempt budget");
    }));
}
