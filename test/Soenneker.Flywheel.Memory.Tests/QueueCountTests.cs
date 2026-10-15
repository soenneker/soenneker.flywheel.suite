using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class QueueCountTests
{
    [Test]
    public async Task CountsAllPendingJobsAndTracksDueTimeAndExecution(CancellationToken cancellationToken)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        for (int i = 0; i < 205; i++)
            await store.Enqueue(new EnqueueRequest("job", "{}", new JobPolicy(), TimeSpan.Zero), cancellationToken);
        for (int i = 0; i < 7; i++)
            await store.Enqueue(new EnqueueRequest("job", "{}", new JobPolicy(), TimeSpan.FromMinutes(1)), cancellationToken);
        if (await store.GetQueueCounts(cancellationToken) != new JobQueueCounts(205, 7))
            throw new Exception("Queue totals were capped or combined future and due jobs.");
        if ((await store.ListScheduled(200, cancellationToken)).Count != 200)
            throw new Exception("Fetching counts changed the bounded job list.");
        clock.Advance(TimeSpan.FromMinutes(1));
        if (await store.GetQueueCounts(cancellationToken) != new JobQueueCounts(212, 0))
            throw new Exception("Jobs due exactly now must be queued without requiring a write.");
        JobLease lease = (await store.ClaimForWorker("worker", TimeSpan.FromMinutes(1), "v1", new HashSet<string> { "job" }, cancellationToken))!;
        if (await store.GetQueueCounts(cancellationToken) != new JobQueueCounts(211, 0))
            throw new Exception("Running jobs must leave the pending count.");
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken);
        if (await store.GetQueueCounts(cancellationToken) != new JobQueueCounts(211, 0))
            throw new Exception("Completed jobs must not enter the queue count.");
    }
}
