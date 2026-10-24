using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class RecoveryPolicyTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    public async Task StaleRestartsHaveTheirOwnBoundedBudget(int restarts, CancellationToken ct)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new(), clock);
        string id = await store.Enqueue(new("job", "{}", new JobPolicy { MaxAttempts = 1, MaxStaleRestarts = restarts,
            InitialBackoff = TimeSpan.FromMilliseconds(1), Jitter = 0 }, TimeSpan.Zero), ct);
        for (int i = 0; i <= restarts; i++)
        {
            JobLease lease = await store.Claim("worker", TimeSpan.FromSeconds(1), ct) ?? throw new Exception("Recovery lost work.");
            clock.Advance(TimeSpan.FromSeconds(2));
            await store.Maintain(100, ct);
            JobRecord job = (await store.Get(id, ct))!;
            Check(job.FailureCount == 0 && job.StaleRestartCount == Math.Min(i + 1, restarts), "Recovery consumed the wrong budget.");
            Check(job.State == (i < restarts ? JobState.Scheduled : JobState.DeadLettered), "Stale recovery limit was not enforced.");
            Check(!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, ct), "Recovered owner bypassed fencing.");
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        Check(await store.Claim("other", TimeSpan.FromSeconds(1), ct) is null, "Poison job can crash-loop indefinitely.");
    }

    [Test]
    public async Task RecoveryDoesNotConsumeFailureRetriesAndCancellationWins(CancellationToken ct)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new(), clock);
        string id = await store.Enqueue(new("job", "{}", new JobPolicy { MaxAttempts = 2,
            InitialBackoff = TimeSpan.FromMilliseconds(1), Jitter = 0 }, TimeSpan.Zero), ct);
        JobLease first = (await store.Claim("first", TimeSpan.FromSeconds(1), ct))!;
        clock.Advance(TimeSpan.FromSeconds(2));
        await store.Maintain(100, ct);
        clock.Advance(TimeSpan.FromSeconds(1));
        JobLease recovered = (await store.Claim("second", TimeSpan.FromSeconds(1), ct))!;
        await store.Finish(recovered, JobOutcome.Failed, "transient", TimeSpan.Zero, ct);
        JobRecord retry = (await store.Get(id, ct))!;
        Check(retry.State == JobState.Scheduled && retry.FailureCount == 1 && retry.StaleRestartCount == 1,
            "Recovery exhausted the failure retry budget.");
        JobLease last = (await store.Claim("third", TimeSpan.FromSeconds(1), ct))!;
        await store.Finish(last, JobOutcome.Failed, "permanent", TimeSpan.Zero, ct);
        Check((await store.Get(id, ct)) is { FailureCount: 2, StaleRestartCount: 1, Attempt: 3 } final && final.State == JobState.DeadLettered,
            "Failed attempts did not exhaust their independent budget.");

        string cancelled = await store.Enqueue(new EnqueueRequest("job", "{}", new JobPolicy(), TimeSpan.Zero), ct);
        await store.Claim("worker", TimeSpan.FromSeconds(1), ct);
        await store.Cancel(cancelled, ct);
        clock.Advance(TimeSpan.FromSeconds(2));
        await store.Maintain(100, ct);
        Check((await store.Get(cancelled, ct))!.State == JobState.Cancelled, "Stale recovery resurrected cancelled work.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
