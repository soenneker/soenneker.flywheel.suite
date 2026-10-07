using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;

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
    public async Task SaturatedPartitionDoesNotBlockOthersAndCompletionReleasesCapacity()
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrencyPerPartition = 1 });
        await store.Enqueue(Request("a"));
        JobLease first = (await store.Claim("one", TimeSpan.FromMinutes(1)))!;
        for (int i = 0; i < 40; i++) await store.Enqueue(Request("a"));
        string other = await store.Enqueue(Request("b"));
        JobLease second = (await store.Claim("two", TimeSpan.FromMinutes(1)))!;
        Check(second.Job.Id == other, "Saturated partition hid eligible work.");
        Check(await store.Claim("three", TimeSpan.FromMinutes(1)) is null, "Partition limit exceeded.");
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check((await store.Claim("three", TimeSpan.FromMinutes(1)))?.Job.Policy.PartitionKey == "a", "Completion leaked capacity.");
    }

    [Test]
    public async Task EqualPriorityPartitionsTakeTurnsAndPriorityStillWins()
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.Enqueue(Request("a"));
        JobLease first = (await store.Claim("one", TimeSpan.FromMinutes(1)))!;
        for (int i = 0; i < 12; i++) await store.Enqueue(Request("a"));
        for (int i = 0; i < 3; i++) await store.Enqueue(Request("b"));
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero);
        for (int i = 0; i < 6; i++)
        {
            JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1)))!;
            Check(lease.Job.Policy.PartitionKey == (i % 2 == 0 ? "b" : "a"), "A busy partition monopolized dispatch.");
            await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero);
        }
        string critical = await store.Enqueue(Request("a", priority: JobPriority.Critical));
        await store.Enqueue(Request("new"));
        Check((await store.Claim("worker", TimeSpan.FromMinutes(1)))!.Job.Id == critical, "Fairness overrode priority.");
    }

    [Test]
    public async Task MethodLimitsAndPartitionScopesCompose()
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrency = 2, MaxConcurrencyPerPartition = 1 });
        await store.Enqueue(Request("a"));
        await store.Enqueue(Request("b"));
        await store.Enqueue(Request("c"));
        Check(await store.Claim("one", TimeSpan.FromMinutes(1)) is not null, "First claim missing.");
        Check(await store.Claim("two", TimeSpan.FromMinutes(1)) is not null, "Second partition blocked.");
        Check(await store.Claim("three", TimeSpan.FromMinutes(1)) is null, "Method limit exceeded.");
        string otherMethod = await store.Enqueue(Request("a", "other"));
        Check((await store.Claim("three", TimeSpan.FromMinutes(1)))!.Job.Id == otherMethod, "Keys collided across methods.");
    }

    [Test]
    public async Task ExpiredLeaseRecoversPayloadAndRejectsStaleOwner()
    {
        var clock = new PartitionTestClock();
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrencyPerPartition = 1 });
        string id = await store.Enqueue(Request("a") with { Payload = "{\"runId\":\"durable\"}" });
        JobLease stale = (await store.Claim("dead", TimeSpan.FromSeconds(3)))!;
        clock.Advance(TimeSpan.FromSeconds(4));
        await store.Maintain(100);
        clock.Advance(TimeSpan.FromSeconds(1));
        JobLease recovered = (await store.Claim("replacement", TimeSpan.FromSeconds(3)))!;
        Check(recovered.Job.Id == id && recovered.Job.Attempt == 2 && recovered.Job.Payload.Contains("durable"), "Recovery lost work.");
        Check(!await store.Finish(stale, JobOutcome.Succeeded, null, TimeSpan.Zero), "Stale owner completed recovered work.");
        Check(await store.Finish(recovered, JobOutcome.Succeeded, null, TimeSpan.Zero), "Recovered owner could not complete.");
    }

    [Test]
    public async Task UnkeyedJobsRemainUnlimitedByPartitionPolicy()
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrencyPerPartition = 1 });
        await store.Enqueue(Request(null));
        await store.Enqueue(Request(null));
        Check(await store.Claim("one", TimeSpan.FromMinutes(1)) is not null, "Unkeyed job missing.");
        Check(await store.Claim("two", TimeSpan.FromMinutes(1)) is not null, "Unkeyed jobs shared a partition.");
    }

    [Test]
    public async Task RetryAndCancellationReleaseCapacityAndPolicyChangesApply()
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrencyPerPartition = 2 });
        for (int i = 0; i < 4; i++) await store.Enqueue(Request("a"));
        JobLease first = (await store.Claim("one", TimeSpan.FromMinutes(1)))!;
        JobLease second = (await store.Claim("two", TimeSpan.FromMinutes(1)))!;
        await store.ConfigureMethod("automation", new MethodPolicy { MaxConcurrencyPerPartition = 1 });
        await store.Finish(first, JobOutcome.Failed, "retry", TimeSpan.FromHours(1));
        Check(await store.Claim("three", TimeSpan.FromMinutes(1)) is null, "Lowered limit ignored a running lease.");
        await store.Cancel(second.Job.Id);
        Check(await store.Claim("three", TimeSpan.FromMinutes(1)) is null, "Cancellation released a still-running handler.");
        await store.Finish(second, JobOutcome.Cancelled, null, TimeSpan.Zero);
        Check(await store.Claim("three", TimeSpan.FromMinutes(1)) is not null, "Retry/cancellation leaked capacity.");
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
