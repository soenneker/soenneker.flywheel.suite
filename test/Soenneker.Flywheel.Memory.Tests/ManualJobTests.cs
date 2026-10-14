using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using System.Threading;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class ManualJobTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public async Task ManualDefinitionsOnlyRunExplicitlyAndRetainLatestStatus(CancellationToken cancellationToken)
    {
        var clock = new PartitionTestClock();
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions { RetainCompletedJobs = false }, clock);
        var request = new EnqueueRequest("manual", "{\"value\":1}", new JobPolicy(), TimeSpan.Zero, Description: "Manual description");
        Check(await store.AddManual("one", request, cancellationToken), "Registration failed");
        Check(!await store.AddManual("one", request with { Payload = "{}" }, cancellationToken), "Duplicate replaced definition");
        clock.Advance(TimeSpan.FromDays(30));
        await store.Maintain(100, cancellationToken);
        Check((await store.List(cancellationToken: cancellationToken)).Count == 0, "Manual job ran automatically");
        Check(await store.GetRecurringCount(cancellationToken) == 0 && (await store.ListRecurring(cancellationToken: cancellationToken)).Count == 0, "Manual definition leaked into recurring jobs");
        Check(await store.RunManual("missing", cancellationToken) is null, "Unknown definition ran");
        string first = (await store.RunManual("one", cancellationToken))!;
        JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        Check(lease.Job.Payload == request.Payload && lease.Job.ManualJobId == "one" && lease.Job.ScheduleId is null, "Execution lost definition metadata");
        Check((await store.ListManual(cancellationToken: cancellationToken)).Single().LastExecutionStatus == "Running", "Missing running status");
        string latest = (await store.RunManual("one", cancellationToken))!;
        Check(first != latest, "Explicit runs must create fresh executions");
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        await store.Maintain(100, cancellationToken);
        Check((await store.ListManual(cancellationToken: cancellationToken)).Single().LastExecutionStatus == "Queued", "Older completion replaced latest status");
        lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        await store.Maintain(100, cancellationToken);
        var definition = (await store.ListManual(cancellationToken: cancellationToken)).Single();
        Check(await store.Get(latest, cancellationToken) is null, "Execution was not cleaned up");
        Check(definition.LastExecutionId == latest && definition.LastExecutionStatus == "Succeeded", "Cleanup lost latest status");
        Check(await store.RunManual("one", cancellationToken) is not null, "Cleanup removed reusable definition");
    }
}
