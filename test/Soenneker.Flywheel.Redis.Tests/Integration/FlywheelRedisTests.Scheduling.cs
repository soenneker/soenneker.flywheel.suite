using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Soenneker.Flywheel.Core.Enums;
using Soenneker.Flywheel.Core.Dtos;
using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Generated;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task GeneratedCronAndTypedChainExecuteThroughJobClient() => WithStore(async store =>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlywheel().AddGeneratedJobs();
        services.AddSingleton<IJobStore>(store);
        services.AddSingleton<InvocationState>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var client = provider.GetRequiredService<IJobClient>();
        await client.RegisterGeneratedSchedules();
        await client.RegisterGeneratedSchedules();
        var schedule = (await store.ListRecurring()).Single();
        Check(schedule.Cron == "0 9 * * *" && schedule.TimeZoneId == "America/Chicago", "Generated cron registration failed");
        await store.RunRecurring(schedule.Id);
        var executor = provider.GetRequiredService<IJobExecutor>();
        await executor.RunOnce(default);
        Check(provider.GetRequiredService<InvocationState>().Value == "cron payload", "Attribute payload was not passed to handler");
        var ids = await client.Chain([
            FlywheelJobs.IntegrationJobs_Run.With(new TestPayload("first")),
            FlywheelJobs.IntegrationJobs_Run.With(new TestPayload("second"))], "generated-chain");
        await executor.RunOnce(default);
        Check((await store.Get(ids[0]))!.State == JobState.Succeeded && (await store.Get(ids[1]))!.State == JobState.Scheduled, "Typed chain did not advance");
        await executor.RunOnce(default);
        Check((await store.Get(ids[1]))!.State == JobState.Succeeded && provider.GetRequiredService<InvocationState>().Value == "second", "Typed chain payload or execution failed");
        int before = (await store.List()).Count;
        try
        {
            await client.Chain([FlywheelJobs.IntegrationJobs_Run.With(new TestPayload("valid")), new JobDefinition<string>("unknown").With("invalid")]);
            throw new Exception("Unregistered step accepted");
        }
        catch (InvalidOperationException) { }
        Check((await store.List()).Count == before, "Invalid catalog wrote a partial chain");
    });

    [Test]
    public Task ChainSubmissionIsAtomicAndDeduplicatedAcrossWorkers() => WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            (i % 2 == 0 ? store : other).EnqueueChain([Request(), Request(), Request()], "chain")));
        Check(results.All(ids => ids.SequenceEqual(results[0])), "Chain submission was not deduplicated");
        Check((await store.List()).Count == 3, "Partial or duplicate chain persisted");
        var waiting = (await store.Get(results[0][1]))!;
        Check(waiting.State == JobState.Waiting && waiting.DueAt == 0 && waiting.ParentJobId == results[0][0], "Waiting step metadata is incorrect");
        var claims = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => other.Claim("node" + i, TimeSpan.FromSeconds(30))));
        Check(claims.Count(x => x is not null) == 1 && claims.Single(x => x is not null)!.Job.Id == results[0][0], "Multiple chain steps became runnable");
        try { await store.EnqueueChain([Request(), Request() with { Payload = "invalid JSON" }]); throw new Exception("Invalid step accepted"); }
        catch (System.Text.Json.JsonException) { }
        Check((await store.List()).Count == 3, "Validation wrote part of a chain");
    });

    [Test]
    public Task ChainRetriesWaitAndSuccessReleasesExactlyOneSuccessor() => WithStore(async (store, db, ns) =>
    {
        IReadOnlyList<string> ids = await store.EnqueueChain([Request(), Request(), Request()]);
        var first = (await store.Claim("first", TimeSpan.FromSeconds(30)))!;
        await store.Finish(first, JobOutcome.Failed, "retry", TimeSpan.Zero);
        Check((await store.Get(ids[1]))!.State == JobState.Waiting, "Retry released next step");
        var retry = (await store.Claim("retry", TimeSpan.FromSeconds(30)))!;
        Check(retry.Job.Id == ids[0] && retry.Job.Attempt == 2, "Wrong job retried");
        bool[] finished = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.Finish(retry, JobOutcome.Succeeded, null, TimeSpan.Zero)));
        Check(finished.Count(x => x) == 1, "Completion committed twice");
        var restarted = new RedisJobStore(_ => Task.FromResult(db), ns);
        var second = (await restarted.Claim("restart", TimeSpan.FromSeconds(30)))!;
        Check(second.Job.Id == ids[1] && (await store.Get(ids[2]))!.State == JobState.Waiting, "Successor missing after restart");
        await restarted.Finish(second, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check((await restarted.Claim("last", TimeSpan.FromSeconds(30)))!.Job.Id == ids[2], "Last step not released");
    });

    [Test]
    public Task ChainFailureAndWaitingCancellationStopRemainingSteps() => WithStore(async store =>
    {
        var failed = await store.EnqueueChain([Request(attempts: 1), Request(), Request()]);
        var first = (await store.Claim("failure", TimeSpan.FromSeconds(30)))!;
        await store.Finish(first, JobOutcome.Failed, "permanent", TimeSpan.Zero);
        Check((await store.Get(failed[0]))!.State == JobState.DeadLettered, "First step did not fail permanently");
        foreach (string id in failed.Skip(1)) Check((await store.Get(id))!.State == JobState.Cancelled, "Failed chain left a waiting step");
        var cancelled = await store.EnqueueChain([Request(), Request(), Request()]);
        var running = (await store.Claim("running", TimeSpan.FromSeconds(30)))!;
        Check(await store.Cancel(cancelled[1]), "Waiting step could not be cancelled");
        await store.Finish(running, JobOutcome.Succeeded, null, TimeSpan.Zero);
        foreach (string id in cancelled.Skip(1)) Check((await store.Get(id))!.State == JobState.Cancelled, "Cancelled suffix was released");
        Check(await store.Claim("none", TimeSpan.FromSeconds(30)) is null, "Cancelled chain ran");
    });

    [Test]
    public Task ChainCancellationWinsCompletionRace() => WithStore(async store =>
    {
        var ids = await store.EnqueueChain([Request(), Request()]);
        var lease = (await store.Claim("cancel", TimeSpan.FromSeconds(30)))!;
        await store.Cancel(ids[0]);
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check((await store.Get(ids[1]))!.State == JobState.Cancelled, "Cancellation lost to success");
    });

    [Test]
    public Task ChainRecoveryRejectsExpiredSuccessAndCancelsAfterFinalAttempt() => WithStore(async store =>
    {
        var ids = await store.EnqueueChain([Request(attempts: 1), Request()]);
        var lease = (await store.Claim("crashed", TimeSpan.FromMilliseconds(80)))!;
        await Task.Delay(150);
        Check(!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Expired owner released successor");
        Check((await store.Get(ids[1]))!.State == JobState.Waiting, "Expired completion changed successor");
        await store.Maintain(100);
        Check((await store.Get(ids[0]))!.State == JobState.DeadLettered && (await store.Get(ids[1]))!.State == JobState.Cancelled,
            "Recovery did not terminate chain");
    });

    [Test]
    public Task ChainSuccessorDelayAndFunctionLimitsAreRespected() => WithStore(async store =>
    {
        await store.ConfigureMethod("test.v1", new MethodPolicy { RateLimit = 1, RateWindow = TimeSpan.FromMinutes(1) });
        var ids = await store.EnqueueChain([Request(), Request() with { Delay = TimeSpan.FromMilliseconds(150) }]);
        var first = (await store.Claim("first", TimeSpan.FromSeconds(30)))!;
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check(await store.Claim("early", TimeSpan.FromSeconds(30)) is null, "Delayed step ran early");
        await Task.Delay(200);
        Check(await store.Claim("limited", TimeSpan.FromSeconds(30)) is null, "Chain bypassed function rate limit");
        await store.ConfigureMethod("test.v1", new MethodPolicy());
        Check((await store.Claim("ready", TimeSpan.FromSeconds(30)))!.Job.Id == ids[1], "Eligible step not released");
    });

    [Test]
    public Task CronSchedulesPersistCoalesceAndMaterializeOnce() => WithStore(async (store, db, ns) =>
    {
        var created = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.AddCron("daily", Request(), "0 9 * * *", "America/Chicago")));
        Check(created.Count(x => x) == 1, "Duplicate cron schedules");
        var schedule = (await store.ListRecurring()).Single();
        Check(schedule.Cron == "0 9 * * *" && schedule.TimeZoneId == "America/Chicago", "Cron metadata lost");
        Check((await store.List()).Count == 0 && schedule.DueAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "Cron ran at registration");
        // Move only this test namespace's due index into the past to simulate scheduler downtime.
        string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        await db.SortedSetAddAsync($"flywheel:{{{tag}}}:v1:schedule-due", schedule.Id, DateTimeOffset.UtcNow.AddDays(-4).ToUnixTimeMilliseconds());
        var restarted = new RedisJobStore(_ => Task.FromResult(db), ns);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => restarted.Maintain(100)));
        Check((await store.List()).Count == 1, "Missed cron ticks were duplicated or replayed as a backlog");
        Check((await store.ListRecurring()).Single().DueAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "Next cron time not advanced");
        Check(!await store.AddCron("daily", Request(), "* * * * *"), "Registration replaced a schedule");
        Check((await store.ListRecurring()).Single().Cron == "0 9 * * *", "Existing expression changed");
        Check(await store.RunRecurring(schedule.Id) is not null, "Manual cron run failed");
    });

    [Test]
    public Task ChainLengthLimitAndFullCancellationAreBounded() => WithStore(async store =>
    {
        try { await store.EnqueueChain(Enumerable.Range(0, 101).Select(_ => Request()).ToArray()); throw new Exception("Oversized chain accepted"); }
        catch (ArgumentException) { }
        Check((await store.List()).Count == 0, "Oversized chain wrote jobs");
        var ids = await store.EnqueueChain(Enumerable.Range(0, 100).Select(_ => Request()).ToArray());
        await store.Cancel(ids[0]);
        var jobs = await store.List(count: 200);
        Check(jobs.Count == 100 && jobs.All(job => job.State == JobState.Cancelled), "Maximum chain did not cancel completely");
        Check(await store.Claim("none", TimeSpan.FromSeconds(30)) is null, "Cancelled maximum chain ran");
    });

    [Test]
    public Task InvalidCronDoesNotWriteAndLegacyIntervalsStillRun() => WithStore(async store =>
    {
        try { await store.AddCron("invalid", Request(), "not cron"); throw new Exception("Bad expression accepted"); }
        catch (FormatException) { }
        try { await store.AddCron("impossible", Request(), "0 0 30 2 *"); throw new Exception("Impossible expression accepted"); }
        catch (ArgumentException) { }
        Check((await store.ListRecurring()).Count == 0, "Invalid cron persisted");
        await store.AddRecurring("interval", Request(), TimeSpan.FromHours(1));
        await store.Maintain(100);
        Check((await store.List()).Count == 1 && (await store.ListRecurring()).Single().Cron is null, "Interval behavior changed");
    });
}
