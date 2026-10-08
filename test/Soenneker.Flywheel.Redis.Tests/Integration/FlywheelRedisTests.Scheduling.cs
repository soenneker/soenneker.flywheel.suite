using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Generated;
using System.Threading;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask GeneratedCronAndTypedChainExecuteThroughJobClient(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        var services = new ServiceCollection();
        services.AddSingleton<System.Text.Json.Serialization.JsonSerializerContext>(TestJsonContext.Default);
        services.AddLogging();
        services.AddFlywheel().AddGeneratedJobs();
        services.AddSingleton<IJobStore>(store);
        services.AddSingleton<InvocationState>();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var client = provider.GetRequiredService<IJobClient>();
        await client.RegisterGeneratedSchedules(TestJsonContext.Default, cancellationToken: cancellationToken);
        await client.RegisterGeneratedSchedules(TestJsonContext.Default, cancellationToken: cancellationToken);
        RecurringJobView schedule = (await store.ListRecurring(cancellationToken: cancellationToken)).Single();
        Check(schedule.Cron == "0 9 * * *" && schedule.TimeZoneId == "America/Chicago", "Generated cron registration failed");
        await store.RunRecurring(schedule.Id, cancellationToken: cancellationToken);
        var executor = provider.GetRequiredService<IJobExecutor>();
        await executor.RunOnce(cancellationToken);
        Check(provider.GetRequiredService<InvocationState>().Value == "cron payload", "Attribute payload was not passed to handler");
        IReadOnlyList<string> ids = await client.Chain([
            FlywheelJobs.IntegrationJobs_Run.With(new TestPayload("first"), TestJsonContext.Get<TestPayload>()),
            FlywheelJobs.IntegrationJobs_Run.With(new TestPayload("second"), TestJsonContext.Get<TestPayload>())], "generated-chain", cancellationToken: cancellationToken);
        await executor.RunOnce(cancellationToken);
        Check((await store.Get(ids[0], cancellationToken: cancellationToken))!.State == JobState.Succeeded && (await store.Get(ids[1], cancellationToken: cancellationToken))!.State == JobState.Scheduled, "Typed chain did not advance");
        await executor.RunOnce(cancellationToken);
        Check((await store.Get(ids[1], cancellationToken: cancellationToken))!.State == JobState.Succeeded && provider.GetRequiredService<InvocationState>().Value == "second", "Typed chain payload or execution failed");
        int before = (await store.List(cancellationToken: cancellationToken)).Count;
        try
        {
            await client.Chain([FlywheelJobs.IntegrationJobs_Run.With(new TestPayload("valid"), TestJsonContext.Get<TestPayload>()), new JobDefinition<string>("unknown").With("invalid", TestJsonContext.Get<string>())], cancellationToken: cancellationToken);
            throw new Exception("Unregistered step accepted");
        }
        catch (InvalidOperationException) { }
        Check((await store.List(cancellationToken: cancellationToken)).Count == before, "Invalid catalog wrote a partial chain");
    }));

    [Test]
    public ValueTask ChainSubmissionIsAtomicAndDeduplicatedAcrossWorkers(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        IReadOnlyList<string>[] results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            (i % 2 == 0 ? store : other).EnqueueChain([Request(), Request(), Request()], "chain", cancellationToken: cancellationToken)));
        Check(results.All(ids => ids.SequenceEqual(results[0])), "Chain submission was not deduplicated");
        Check((await store.List(cancellationToken: cancellationToken)).Count == 3, "Partial or duplicate chain persisted");
        JobRecord waiting = (await store.Get(results[0][1], cancellationToken: cancellationToken))!;
        Check(waiting.State == JobState.Waiting && waiting.DueAt == 0 && waiting.ParentJobId == results[0][0], "Waiting step metadata is incorrect");
        JobLease?[] claims = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => other.Claim("node" + i, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken)));
        Check(claims.Count(x => x is not null) == 1 && claims.Single(x => x is not null)!.Job.Id == results[0][0], "Multiple chain steps became runnable");
        try { await store.EnqueueChain([Request(), Request() with { Payload = "invalid JSON" }], cancellationToken: cancellationToken); throw new Exception("Invalid step accepted"); }
        catch (System.Text.Json.JsonException) { }
        Check((await store.List(cancellationToken: cancellationToken)).Count == 3, "Validation wrote part of a chain");
    }));

    [Test]
    public ValueTask ChainRetriesWaitAndSuccessReleasesExactlyOneSuccessor(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        IReadOnlyList<string> ids = await store.EnqueueChain([Request(), Request(), Request()], cancellationToken: cancellationToken);
        JobLease first = (await store.Claim("first", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        await store.Finish(first, JobOutcome.Failed, "retry", TimeSpan.Zero, cancellationToken: cancellationToken);
        Check((await store.Get(ids[1], cancellationToken: cancellationToken))!.State == JobState.Waiting, "Retry released next step");
        JobLease retry = (await store.Claim("retry", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check(retry.Job.Id == ids[0] && retry.Job.Attempt == 2, "Wrong job retried");
        bool[] finished = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.Finish(retry, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken)));
        Check(finished.Count(x => x) == 1, "Completion committed twice");
        var restarted = new RedisJobStore(_ => Task.FromResult(db), ns);
        JobLease second = (await restarted.Claim("restart", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check(second.Job.Id == ids[1] && (await store.Get(ids[2], cancellationToken: cancellationToken))!.State == JobState.Waiting, "Successor missing after restart");
        await restarted.Finish(second, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check((await restarted.Claim("last", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!.Job.Id == ids[2], "Last step not released");
    }));

    [Test]
    public ValueTask ChainFailureAndWaitingCancellationStopRemainingSteps(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        IReadOnlyList<string> failed = await store.EnqueueChain([Request(attempts: 1), Request(), Request()], cancellationToken: cancellationToken);
        JobLease first = (await store.Claim("failure", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        await store.Finish(first, JobOutcome.Failed, "permanent", TimeSpan.Zero, cancellationToken: cancellationToken);
        Check((await store.Get(failed[0], cancellationToken: cancellationToken))!.State == JobState.DeadLettered, "First step did not fail permanently");
        foreach (string id in failed.Skip(1)) Check((await store.Get(id, cancellationToken: cancellationToken))!.State == JobState.Cancelled, "Failed chain left a waiting step");
        IReadOnlyList<string> cancelled = await store.EnqueueChain([Request(), Request(), Request()], cancellationToken: cancellationToken);
        JobLease running = (await store.Claim("running", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check(await store.Cancel(cancelled[1], cancellationToken: cancellationToken), "Waiting step could not be cancelled");
        await store.Finish(running, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        foreach (string id in cancelled.Skip(1)) Check((await store.Get(id, cancellationToken: cancellationToken))!.State == JobState.Cancelled, "Cancelled suffix was released");
        Check(await store.Claim("none", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null, "Cancelled chain ran");
    }));

    [Test]
    public ValueTask ChainCancellationWinsCompletionRace(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        IReadOnlyList<string> ids = await store.EnqueueChain([Request(), Request()], cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("cancel", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        await store.Cancel(ids[0], cancellationToken: cancellationToken);
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check((await store.Get(ids[1], cancellationToken: cancellationToken))!.State == JobState.Cancelled, "Cancellation lost to success");
    }));

    [Test]
    public ValueTask ChainRecoveryRejectsExpiredSuccessAndCancelsAfterFinalAttempt(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        IReadOnlyList<string> ids = await store.EnqueueChain([Request(attempts: 1), Request()], cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("crashed", TimeSpan.FromMilliseconds(80), cancellationToken: cancellationToken))!;
        await Task.Delay(150, cancellationToken: cancellationToken);
        Check(!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Expired owner released successor");
        Check((await store.Get(ids[1], cancellationToken: cancellationToken))!.State == JobState.Waiting, "Expired completion changed successor");
        await store.Maintain(100, cancellationToken: cancellationToken);
        Check((await store.Get(ids[0], cancellationToken: cancellationToken))!.State == JobState.DeadLettered && (await store.Get(ids[1], cancellationToken: cancellationToken))!.State == JobState.Cancelled,
            "Recovery did not terminate chain");
    }));

    [Test]
    public ValueTask ChainSuccessorDelayAndFunctionLimitsAreRespected(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        await store.ConfigureMethod("test.v1", new MethodPolicy { RateLimit = 1, RateWindow = TimeSpan.FromMinutes(1) }, cancellationToken: cancellationToken);
        IReadOnlyList<string> ids = await store.EnqueueChain([Request(), Request() with { Delay = TimeSpan.FromMilliseconds(150) }], cancellationToken: cancellationToken);
        JobLease first = (await store.Claim("first", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check(await store.Claim("early", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null, "Delayed step ran early");
        await Task.Delay(200, cancellationToken: cancellationToken);
        Check(await store.Claim("limited", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null, "Chain bypassed function rate limit");
        await store.ConfigureMethod("test.v1", new MethodPolicy(), cancellationToken: cancellationToken);
        Check((await store.Claim("ready", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!.Job.Id == ids[1], "Eligible step not released");
    }));

    [Test]
    public ValueTask CronSchedulesPersistCoalesceAndMaterializeOnce(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        bool[] created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.AddCron("daily", Request(), "0 9 * * *", "America/Chicago", cancellationToken: cancellationToken)));
        Check(created.Count(x => x) == 1, "Duplicate cron schedules");
        RecurringJobView schedule = (await store.ListRecurring(cancellationToken: cancellationToken)).Single();
        Check(schedule.Cron == "0 9 * * *" && schedule.TimeZoneId == "America/Chicago", "Cron metadata lost");
        Check((await store.List(cancellationToken: cancellationToken)).Count == 0 && schedule.DueAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "Cron ran at registration");
        // Move only this test namespace's due index into the past to simulate scheduler downtime.
        await using var database = OpenLibrarian(db, ns);
        var schedules = await database.GetContainer("flywheel.schedules", cancellationToken: cancellationToken);
        string raw = (await schedules.GetItem(DocumentId(schedule.Id), cancellationToken: cancellationToken))!;
        var document = System.Text.Json.Nodes.JsonNode.Parse(raw)!;
        document["value"]!["dueAt"] = DateTimeOffset.UtcNow.AddDays(-4).ToUnixTimeMilliseconds();
        await schedules.UpdateItemStrict(DocumentId(schedule.Id), document.ToJsonString(), cancellationToken: cancellationToken);
        await using var restarted = new RedisJobStore(_ => Task.FromResult(db), ns);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => restarted.Maintain(100, cancellationToken: cancellationToken)));
        Check((await store.List(cancellationToken: cancellationToken)).Count == 1, "Missed cron ticks were duplicated or replayed as a backlog");
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Single().DueAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "Next cron time not advanced");
        Check(!await store.AddCron("daily", Request(), "* * * * *", cancellationToken: cancellationToken), "Registration replaced a schedule");
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Single().Cron == "0 9 * * *", "Existing expression changed");
        Check(await store.RunRecurring(schedule.Id, cancellationToken: cancellationToken) is not null, "Manual cron run failed");
    }));

    [Test]
    public ValueTask ChainLengthLimitAndFullCancellationAreBounded(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        try { await store.EnqueueChain(Enumerable.Range(0, 101).Select(_ => Request()).ToArray(), cancellationToken: cancellationToken); throw new Exception("Oversized chain accepted"); }
        catch (ArgumentException) { }
        Check((await store.List(cancellationToken: cancellationToken)).Count == 0, "Oversized chain wrote jobs");
        IReadOnlyList<string> ids = await store.EnqueueChain(Enumerable.Range(0, 100).Select(_ => Request()).ToArray(), cancellationToken: cancellationToken);
        await store.Cancel(ids[0], cancellationToken: cancellationToken);
        IReadOnlyList<JobRecord> jobs = await store.List(count: 200, cancellationToken: cancellationToken);
        Check(jobs.Count == 100 && jobs.All(job => job.State == JobState.Cancelled), "Maximum chain did not cancel completely");
        Check(await store.Claim("none", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null, "Cancelled maximum chain ran");
    }));

    [Test]
    public ValueTask InvalidCronDoesNotWriteAndIntervalsStillRun(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        try { await store.AddCron("invalid", Request(), "not cron", cancellationToken: cancellationToken); throw new Exception("Bad expression accepted"); }
        catch (FormatException) { }
        try { await store.AddCron("impossible", Request(), "0 0 30 2 *", cancellationToken: cancellationToken); throw new Exception("Impossible expression accepted"); }
        catch (ArgumentException) { }
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Count == 0, "Invalid cron persisted");
        await store.AddRecurring("interval", Request(), TimeSpan.FromHours(1), cancellationToken: cancellationToken);
        await store.Maintain(100, cancellationToken: cancellationToken);
        Check((await store.List(cancellationToken: cancellationToken)).Count == 1 && (await store.ListRecurring(cancellationToken: cancellationToken)).Single().Cron is null, "Interval behavior changed");
    }));
}
