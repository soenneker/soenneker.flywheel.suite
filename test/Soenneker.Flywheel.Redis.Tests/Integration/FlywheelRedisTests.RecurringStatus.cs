using System;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task RecurringStatusTracksLatestExecutionAndSurvivesCleanup() => WithStore(async (store, db, ns) =>
    {
        await store.AddRecurring("status", Request(), TimeSpan.FromHours(1));
        Check((await store.ListRecurring()).Single().LastExecutionStatus is null, "Unrun schedule has an execution status");
        await store.Maintain(10);
        Check((await store.ListRecurring()).Single().LastExecutionStatus == "Queued", "Automatic execution was not tracked");
        JobLease first = (await store.Claim("first", TimeSpan.FromSeconds(30)))!;
        Check((await store.ListRecurring()).Single().LastExecutionStatus == "Running", "Running status missing");
        string scheduleId = (await store.ListRecurring()).Single().Id;
        string? latestId = await store.RunRecurring(scheduleId);
        Check(latestId is not null && latestId != first.Job.Id, "Manual run did not create a separate execution");
        Check((await store.ListRecurring()).Single().LastExecutionStatus == "Queued", "Manual execution was not tracked");
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check((await store.ListRecurring()).Single().LastExecutionStatus == "Queued", "Older completion replaced latest execution");
        JobLease latest = (await store.Claim("latest", TimeSpan.FromSeconds(30)))!;
        Check(latest.Job.Id == latestId, "Claim did not return the latest execution");
        await store.Finish(latest, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check((await store.ListRecurring()).Single().LastExecutionStatus == "Succeeded", "Completion status missing");
        var cleanup = new RedisJobStore(_ => Task.FromResult(db), ns, retainCompletedJobs: false);
        await cleanup.Maintain(10);
        Check(await store.Get(latest.Job.Id) is null, "Completed execution was not pruned");
        Check((await store.ListRecurring()).Single().LastExecutionStatus == "Succeeded", "Cleanup lost latest status");
    });
}
