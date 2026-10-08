using System;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using System.Threading;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask RecurringStatusTracksLatestExecutionAndSurvivesCleanup(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        await store.AddRecurring("status", Request(), TimeSpan.FromHours(1), cancellationToken: cancellationToken);
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Single().LastExecutionStatus is null, "Unrun schedule has an execution status");
        await store.Maintain(10, cancellationToken: cancellationToken);
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Single().LastExecutionStatus == "Queued", "Automatic execution was not tracked");
        JobLease first = (await store.Claim("first", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Single().LastExecutionStatus == "Running", "Running status missing");
        string scheduleId = (await store.ListRecurring(cancellationToken: cancellationToken)).Single().Id;
        string? latestId = await store.RunRecurring(scheduleId, cancellationToken: cancellationToken);
        Check(latestId is not null && latestId != first.Job.Id, "Manual run did not create a separate execution");
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Single().LastExecutionStatus == "Queued", "Manual execution was not tracked");
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Single().LastExecutionStatus == "Queued", "Older completion replaced latest execution");
        JobLease latest = (await store.Claim("latest", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check(latest.Job.Id == latestId, "Claim did not return the latest execution");
        await store.Finish(latest, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Single().LastExecutionStatus == "Succeeded", "Completion status missing");
        var cleanup = new RedisJobStore(_ => Task.FromResult(db), ns, retainCompletedJobs: false);
        await cleanup.Maintain(10, cancellationToken: cancellationToken);
        Check(await store.Get(latest.Job.Id, cancellationToken: cancellationToken) is null, "Completed execution was not pruned");
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Single().LastExecutionStatus == "Succeeded", "Cleanup lost latest status");
    }));
}
