using System;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask QueueMovesUsePersistedDispatchOrder(CancellationToken cancellationToken) => new(WithStore(async store =>
    {
        string first = await store.Enqueue(Request(), cancellationToken);
        string top = await store.Enqueue(Request() with { Policy = Request().Policy with { PartitionKey = "A", Priority = JobPriority.Low } }, cancellationToken);
        string bottom = await store.Enqueue(Request() with { Policy = Request().Policy with { PartitionKey = "B", Priority = JobPriority.Critical } }, cancellationToken);
        Check(await store.UpdateQueue(top, JobQueueAction.MoveToTop, cancellationToken), "Top move failed");
        Check(await store.UpdateQueue(bottom, JobQueueAction.MoveToBottom, cancellationToken), "Bottom move failed");
        string arrival = await store.Enqueue(Request(), cancellationToken);
        foreach (string id in new[] { top, first, bottom, arrival })
            Check((await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken))?.Job.Id == id, "Persisted dispatch order ignored queue rank");
        Check(!await store.UpdateQueue(top, JobQueueAction.MoveToBottom, cancellationToken), "Running job was reordered");
    }));

    [Test]
    public ValueTask QueueRunNowReleasesOnlyTheSelectedExecution(CancellationToken cancellationToken) => new(WithStore(async store =>
    {
        string first = await store.Enqueue(Request() with { Delay = TimeSpan.FromHours(1) }, cancellationToken);
        string second = await store.Enqueue(Request() with { Delay = TimeSpan.FromHours(1) }, cancellationToken);
        Check(!await store.UpdateQueue(first, JobQueueAction.MoveToTop, cancellationToken), "Top move bypassed schedule");
        Check(await store.UpdateQueue(second, JobQueueAction.RunNow, cancellationToken), "Run now failed");
        Check((await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken))?.Job.Id == second, "Wrong execution released");
        Check(await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken) is null, "Run now released unrelated scheduled work");
        Check((await store.List(cancellationToken: cancellationToken)).Count == 2, "Run now duplicated an execution");
    }));
}
