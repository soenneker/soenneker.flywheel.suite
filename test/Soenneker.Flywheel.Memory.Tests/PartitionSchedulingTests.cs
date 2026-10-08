using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using System.Threading;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class PartitionSchedulingTests
{
    private static EnqueueRequest Request(string? key, string name = "automation", JobPriority? priority = null) => new(name, "{}", new JobPolicy
        {
            PartitionKey = key, MaxAttempts = 3, Priority = priority ?? JobPriority.Normal,
            InitialBackoff = TimeSpan.FromMilliseconds(1)
        }, TimeSpan.Zero);

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    [Test]
    public async Task SaturatedPartitionDoesNotBlockOthersAndCompletionReleasesCapacity(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrencyPerPartition = 1 }, cancellationToken: cancellationToken);
        await store.Enqueue(Request("a"), cancellationToken: cancellationToken);
        JobLease first = (await store.Claim("one", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        for (int i = 0; i < 40; i++) await store.Enqueue(Request("a"), cancellationToken: cancellationToken);
        string other = await store.Enqueue(Request("b"), cancellationToken: cancellationToken);
        JobLease second = (await store.Claim("two", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        Check(second.Job.Id == other, "Saturated partition hid eligible work.");
        Check(await store.Claim("three", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is null, "Partition limit exceeded.");
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check((await store.Claim("three", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))?.Job.Policy.PartitionKey == "a", "Completion leaked capacity.");
    }

    [Test]
    public async Task EqualPriorityPartitionsTakeTurnsAndPriorityStillWins(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.Enqueue(Request("a"), cancellationToken: cancellationToken);
        JobLease first = (await store.Claim("one", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        for (int i = 0; i < 12; i++) await store.Enqueue(Request("a"), cancellationToken: cancellationToken);
        for (int i = 0; i < 3; i++) await store.Enqueue(Request("b"), cancellationToken: cancellationToken);
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        for (int i = 0; i < 6; i++)
        {
            JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
            Check(lease.Job.Policy.PartitionKey == (i % 2 == 0 ? "b" : "a"), "A busy partition monopolized dispatch.");
            await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        }
        string critical = await store.Enqueue(Request("a", priority: JobPriority.Critical), cancellationToken: cancellationToken);
        await store.Enqueue(Request("new"), cancellationToken: cancellationToken);
        Check((await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!.Job.Id == critical, "Fairness overrode priority.");
    }

    [Test]
    public async Task MethodLimitsAndPartitionScopesCompose(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrency = 2, MaxConcurrencyPerPartition = 1 }, cancellationToken: cancellationToken);
        await store.Enqueue(Request("a"), cancellationToken: cancellationToken);
        await store.Enqueue(Request("b"), cancellationToken: cancellationToken);
        await store.Enqueue(Request("c"), cancellationToken: cancellationToken);
        Check(await store.Claim("one", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is not null, "First claim missing.");
        Check(await store.Claim("two", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is not null, "Second partition blocked.");
        Check(await store.Claim("three", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is null, "Method limit exceeded.");
        string otherMethod = await store.Enqueue(Request("a", "other"), cancellationToken: cancellationToken);
        Check((await store.Claim("three", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!.Job.Id == otherMethod, "Keys collided across methods.");
    }

    [Test]
    public async Task ExpiredLeaseRecoversPayloadAndRejectsStaleOwner(CancellationToken cancellationToken)
    {
        var clock = new PartitionTestClock();
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrencyPerPartition = 1 }, cancellationToken: cancellationToken);
        string id = await store.Enqueue(Request("a") with { Payload = "{\"runId\":\"durable\"}" }, cancellationToken: cancellationToken);
        JobLease stale = (await store.Claim("dead", TimeSpan.FromSeconds(3), cancellationToken: cancellationToken))!;
        clock.Advance(TimeSpan.FromSeconds(4));
        await store.Maintain(100, cancellationToken: cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        JobLease recovered = (await store.Claim("replacement", TimeSpan.FromSeconds(3), cancellationToken: cancellationToken))!;
        Check(recovered.Job.Id == id && recovered.Job.Attempt == 2 && recovered.Job.Payload.Contains("durable"), "Recovery lost work.");
        Check(!await store.Finish(stale, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Stale owner completed recovered work.");
        Check(await store.Finish(recovered, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Recovered owner could not complete.");
    }

    [Test]
    public async Task UnkeyedJobsRemainUnlimitedByPartitionPolicy(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrencyPerPartition = 1 }, cancellationToken: cancellationToken);
        await store.Enqueue(Request(null), cancellationToken: cancellationToken);
        await store.Enqueue(Request(null), cancellationToken: cancellationToken);
        Check(await store.Claim("one", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is not null, "Unkeyed job missing.");
        Check(await store.Claim("two", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is not null, "Unkeyed jobs shared a partition.");
    }

    [Test]
    public async Task RetryAndCancellationReleaseCapacityAndPolicyChangesApply(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrencyPerPartition = 2 }, cancellationToken: cancellationToken);
        for (int i = 0; i < 4; i++) await store.Enqueue(Request("a"), cancellationToken: cancellationToken);
        JobLease first = (await store.Claim("one", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        JobLease second = (await store.Claim("two", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrencyPerPartition = 1 }, cancellationToken: cancellationToken);
        await store.Finish(first, JobOutcome.Failed, "retry", TimeSpan.FromHours(1), cancellationToken: cancellationToken);
        Check(await store.Claim("three", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is null, "Lowered limit ignored a running lease.");
        await store.Cancel(second.Job.Id, cancellationToken: cancellationToken);
        Check(await store.Claim("three", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is null, "Cancellation released a still-running handler.");
        await store.Finish(second, JobOutcome.Cancelled, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check(await store.Claim("three", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is not null, "Retry/cancellation leaked capacity.");
    }

    [Test]
    public void InvalidPartitionPoliciesAreRejected()
    {
        foreach (string key in new[] { "", " ", new string('x', 201) })
        {
            bool rejected = false;
            try { new JobPolicy { PartitionKey = key }.Validate(); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Invalid partition key accepted.");
        }
        bool invalidLimit = false;
        try { new MethodPolicy { MaxConcurrencyPerPartition = 0 }.Validate(); }
        catch (ArgumentOutOfRangeException) { invalidLimit = true; }
        Check(invalidLimit, "Invalid partition concurrency accepted.");
    }
}
