using System;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask PartitionLimitsAreAtomicAcrossStores() => new(WithStore(async (store, db, ns) =>
    {
        await using var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrencyPerPartition = 2 });
        for (int i = 0; i < 12; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "tenant:\"雪", MaxAttempts = 3 } });
        JobLease?[] leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            (i % 2 == 0 ? store : other).Claim($"worker-{i}", TimeSpan.FromMinutes(1))));
        Check(leases.Count(lease => lease is not null) == 2, "Concurrent stores exceeded the partition limit.");
        string secondTenant = await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "other" } });
        JobLease next = (await other.Claim("other-tenant", TimeSpan.FromMinutes(1)))!;
        Check(next.Job.Id == secondTenant, "Busy tenant blocked another tenant.");
        JobLease released = leases.First(lease => lease is not null)!;
        await other.Finish(released, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check((await store.Claim("replacement", TimeSpan.FromMinutes(1)))?.Job.Policy.PartitionKey == "tenant:\"雪", "Capacity was not released.");
        string unkeyed = await store.Enqueue(Request());
        Check((await other.Claim("unkeyed", TimeSpan.FromMinutes(1)))?.Job.Id == unkeyed, "Blocked partitions hid unkeyed work.");
    }));

    [Test]
    public ValueTask PartitionFairnessPersistsAcrossWorkersAndSkipsIneligibleHeads() => new(WithStore(async (store, db, ns) =>
    {
        await using var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "a" } });
        JobLease initial = (await store.Claim("first", TimeSpan.FromMinutes(1)))!;
        for (int i = 0; i < 8; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "a" } });
        for (int i = 0; i < 3; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "b" } });
        await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "future" }, Delay = TimeSpan.FromHours(1) });
        await store.EnqueueForCurrentInstance(Request() with { Policy = new JobPolicy { PartitionKey = "private" } }, "v", "absent");
        await store.Finish(initial, JobOutcome.Succeeded, null, TimeSpan.Zero);
        for (int i = 0; i < 6; i++)
        {
            var worker = i % 2 == 0 ? other : store;
            JobLease lease = (await worker.Claim("worker", TimeSpan.FromMinutes(1)))!;
            Check(lease.Job.Policy.PartitionKey == (i % 2 == 0 ? "b" : "a"), "Fairness was not shared or ineligible work blocked dispatch.");
            await worker.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero);
        }
    }));

    [Test]
    public ValueTask PartitionRecoveryReclaimsPayloadAndFencesCrashedWorker() => new(WithStore(async (store, db, ns) =>
    {
        await using var replacement = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrencyPerPartition = 1 });
        string id = await store.Enqueue(Request() with
        {
            Payload = "{\"runId\":\"persistent\"}",
            Policy = new JobPolicy { PartitionKey = "a", MaxAttempts = 3, InitialBackoff = TimeSpan.FromMilliseconds(1) }
        });
        JobLease crashed = (await store.Claim("crashed", TimeSpan.FromMilliseconds(100)))!;
        await Task.Delay(150);
        await replacement.Maintain(100);
        await Task.Delay(10);
        JobLease recovered = (await replacement.Claim("replacement", TimeSpan.FromMinutes(1)))!;
        Check(recovered.Job.Id == id && recovered.Job.Attempt == 2 && recovered.Job.Payload.Contains("persistent"), "Recovery lost payload or retry state.");
        Check(!await store.Finish(crashed, JobOutcome.Succeeded, null, TimeSpan.Zero), "Crashed worker completed replacement's work.");
        await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "a" } });
        Check(await store.Claim("extra", TimeSpan.FromMinutes(1)) is null, "Recovery lost the partition permit.");
        await replacement.Finish(recovered, JobOutcome.Succeeded, null, TimeSpan.Zero);
        Check(await store.Claim("next", TimeSpan.FromMinutes(1)) is not null, "Recovered completion leaked capacity.");
    }));
}
