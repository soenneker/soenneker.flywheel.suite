using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class QueueOrderingTests
{
    private static EnqueueRequest Request(string name = "job", JobPriority? priority = null, string? partition = null) =>
        new(name, "{}", new JobPolicy { Priority = priority ?? JobPriority.Normal, PartitionKey = partition, MaxAttempts = 2 }, TimeSpan.Zero);

    [Test]
    public async Task TopOverridesPriorityAndLatestMoveWins(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        string critical = await store.Enqueue(Request(priority: JobPriority.Critical), cancellationToken);
        string first = await store.Enqueue(Request(priority: JobPriority.Low), cancellationToken);
        string last = await store.Enqueue(Request(priority: JobPriority.Low), cancellationToken);
        if (!await store.UpdateQueue(first, JobQueueAction.MoveToTop, cancellationToken) ||
            !await store.UpdateQueue(last, JobQueueAction.MoveToTop, cancellationToken)) throw new Exception("Queue move rejected.");
        foreach (string id in new[] { last, first, critical })
            if ((await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken))?.Job.Id != id)
                throw new Exception("Top moves did not override normal dispatch priority.");
    }

    [Test]
    public async Task BottomMovesBehindExistingWorkButAheadOfNewArrivals(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        string moved = await store.Enqueue(Request(priority: JobPriority.Critical), cancellationToken);
        string existing = await store.Enqueue(Request(priority: JobPriority.Low), cancellationToken);
        await store.UpdateQueue(moved, JobQueueAction.MoveToBottom, cancellationToken);
        string arrival = await store.Enqueue(Request(priority: JobPriority.Critical), cancellationToken);
        foreach (string id in new[] { existing, moved, arrival })
            if ((await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken))?.Job.Id != id)
                throw new Exception("Bottom move was ignored or starved by new arrivals.");
    }

    [Test]
    public async Task RunNowReleasesDelayWithoutDuplicatingOrReorderingRunningWork(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        string id = await store.Enqueue(Request() with { Delay = TimeSpan.FromHours(1) }, cancellationToken);
        if (await store.UpdateQueue(id, JobQueueAction.MoveToTop, cancellationToken) ||
            await store.UpdateQueue(id, JobQueueAction.MoveToBottom, cancellationToken)) throw new Exception("Queue move released a future job.");
        if (!await store.UpdateQueue(id, JobQueueAction.RunNow, cancellationToken)) throw new Exception("Run now failed.");
        if ((await store.Get(id, cancellationToken))!.Attempt != 0 || (await store.List(cancellationToken: cancellationToken)).Count != 1)
            throw new Exception("Run now consumed an attempt or created a duplicate.");
        var lease = (await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken))!;
        if (lease.Job.Id != id || await store.UpdateQueue(id, JobQueueAction.MoveToTop, cancellationToken))
            throw new Exception("Running work was reordered.");
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken);
        if (await store.UpdateQueue(id, JobQueueAction.RunNow, cancellationToken) ||
            await store.UpdateQueue("missing", JobQueueAction.MoveToBottom, cancellationToken)) throw new Exception("Unavailable work accepted a queue action.");
        var chain = await store.EnqueueChain([Request(), Request()], cancellationToken: cancellationToken);
        string cancelled = await store.Enqueue(Request(), cancellationToken);
        await store.Cancel(cancelled, cancellationToken);
        foreach (JobQueueAction action in Enum.GetValues<JobQueueAction>())
            if (await store.UpdateQueue(chain[1], action, cancellationToken) || await store.UpdateQueue(cancelled, action, cancellationToken))
                throw new Exception("Queue action released a waiting chain step or cancelled execution.");
    }

    [Test]
    public async Task PartitionFairnessCannotUndoAnExplicitMoveAndRetriesResetIt(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        string previous = await store.Enqueue(Request(partition: "A"), cancellationToken);
        var prior = (await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken))!;
        await store.Finish(prior, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken);
        string normal = await store.Enqueue(Request(partition: "B"), cancellationToken);
        string moved = await store.Enqueue(Request(partition: "A"), cancellationToken);
        await store.UpdateQueue(moved, JobQueueAction.MoveToTop, cancellationToken);
        var lease = (await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken))!;
        if (lease.Job.Id != moved) throw new Exception("Partition fairness undid the top move.");
        await store.Finish(lease, JobOutcome.Failed, "retry", TimeSpan.Zero, cancellationToken);
        if ((await store.Get(moved, cancellationToken))!.QueueRank != 0) throw new Exception("Retry retained an operator override.");
        if ((await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken))?.Job.Id != normal)
            throw new Exception("Retry jumped ahead of ordinary waiting work.");
    }

    [Test]
    public async Task TopMoveStillHonorsPoolAndConcurrencyRestrictions(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.ConfigureMethod("restricted", new MethodPolicy { Pools = ["X"], MaxConcurrency = 1 }, cancellationToken);
        var names = new HashSet<string> { "restricted", "ordinary" };
        await store.Enqueue(Request("restricted"), cancellationToken);
        var lease = (await store.ClaimForWorker("x", TimeSpan.FromMinutes(1), "v1", names, "X", cancellationToken))!;
        string moved = await store.Enqueue(Request("restricted"), cancellationToken);
        string normal = await store.Enqueue(Request("ordinary"), cancellationToken);
        await store.UpdateQueue(moved, JobQueueAction.MoveToTop, cancellationToken);
        if ((await store.ClaimForWorker("default", TimeSpan.FromMinutes(1), "v1", names, cancellationToken))?.Job.Id != normal)
            throw new Exception("Queue move bypassed pool routing.");
        if (await store.ClaimForWorker("x", TimeSpan.FromMinutes(1), "v1", names, "X", cancellationToken) is not null)
            throw new Exception("Queue move bypassed concurrency limits.");
        if ((await store.Get(moved, cancellationToken))!.Attempt != 0) throw new Exception("Ineligible move consumed attempts.");
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken);
        if ((await store.ClaimForWorker("x", TimeSpan.FromMinutes(1), "v1", names, "X", cancellationToken))?.Job.Id != moved)
            throw new Exception("Eligible moved job was not claimed.");
    }
}
