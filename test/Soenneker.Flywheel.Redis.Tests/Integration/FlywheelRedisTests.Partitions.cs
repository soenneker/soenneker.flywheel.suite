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
    public ValueTask PartitionLimitsAreAtomicAcrossStores(CancellationToken cancellationToken) => new(WithStore(async (store, db, ns) =>
    {
        await using var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrencyPerPartition = 2 }, cancellationToken: cancellationToken);
        for (int i = 0; i < 12; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "tenant:\"雪", MaxAttempts = 3 } }, cancellationToken: cancellationToken);
        JobLease?[] leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            (i % 2 == 0 ? store : other).Claim($"worker-{i}", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken)));
        Check(leases.Count(lease => lease is not null) == 2, "Concurrent stores exceeded the partition limit.");
        string secondTenant = await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "other" } }, cancellationToken: cancellationToken);
        JobLease next = (await other.Claim("other-tenant", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        Check(next.Job.Id == secondTenant, "Busy tenant blocked another tenant.");
        JobLease released = leases.First(lease => lease is not null)!;
        await other.Finish(released, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check((await store.Claim("replacement", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))?.Job.Policy.PartitionKey == "tenant:\"雪", "Capacity was not released.");
        string unkeyed = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        Check((await other.Claim("unkeyed", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))?.Job.Id == unkeyed, "Blocked partitions hid unkeyed work.");
    }));

    [Test]
    public ValueTask PartitionFairnessPersistsAcrossWorkersAndSkipsIneligibleHeads(CancellationToken cancellationToken) => new(WithStore(async (store, db, ns) =>
    {
        await using var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "a" } }, cancellationToken: cancellationToken);
        JobLease initial = (await store.Claim("first", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        for (int i = 0; i < 8; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "a" } }, cancellationToken: cancellationToken);
        for (int i = 0; i < 3; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "b" } }, cancellationToken: cancellationToken);
        await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "future" }, Delay = TimeSpan.FromHours(1) }, cancellationToken: cancellationToken);
        await store.EnqueueForCurrentInstance(Request() with { Policy = new JobPolicy { PartitionKey = "private" } }, "v", "absent", cancellationToken: cancellationToken);
        await store.Finish(initial, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        for (int i = 0; i < 6; i++)
        {
            var worker = i % 2 == 0 ? other : store;
            JobLease lease = (await worker.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
            Check(lease.Job.Policy.PartitionKey == (i % 2 == 0 ? "b" : "a"), "Fairness was not shared or ineligible work blocked dispatch.");
            await worker.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        }
    }));

    [Test]
    public ValueTask PartitionRecoveryReclaimsPayloadAndFencesCrashedWorker(CancellationToken cancellationToken) => new(WithStore(async (store, db, ns) =>
    {
        await using var replacement = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrencyPerPartition = 1 }, cancellationToken: cancellationToken);
        string id = await store.Enqueue(Request() with
        {
            Payload = "{\"runId\":\"persistent\"}",
            Policy = new JobPolicy { PartitionKey = "a", MaxAttempts = 3, InitialBackoff = TimeSpan.FromMilliseconds(1) }
        }, cancellationToken: cancellationToken);
        JobLease crashed = (await store.Claim("crashed", TimeSpan.FromMilliseconds(100), cancellationToken: cancellationToken))!;
        await Task.Delay(150, cancellationToken: cancellationToken);
        await replacement.Maintain(100, cancellationToken: cancellationToken);
        await Task.Delay(10, cancellationToken: cancellationToken);
        JobLease recovered = (await replacement.Claim("replacement", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        Check(recovered.Job.Id == id && recovered.Job.Attempt == 2 && recovered.Job.Payload.Contains("persistent"), "Recovery lost payload or retry state.");
        Check(!await store.Finish(crashed, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Crashed worker completed replacement's work.");
        await store.Enqueue(Request() with { Policy = new JobPolicy { PartitionKey = "a" } }, cancellationToken: cancellationToken);
        Check(await store.Claim("extra", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is null, "Recovery lost the partition permit.");
        await replacement.Finish(recovered, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        Check(await store.Claim("next", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is not null, "Recovered completion leaked capacity.");
    }));
}
