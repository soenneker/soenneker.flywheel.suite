using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask IndependentHostsCoalesceRecurringRunsAtomically(CancellationToken ct) => new(WithStore(async (store, db, ns) =>
    {
        await using var second = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.AddRecurring("scan", Request(), TimeSpan.FromHours(1), ct, coalesceOccurrences: true);
        string?[] ids = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            (i % 2 == 0 ? store : second).RunRecurring("scan", ct)));
        Check(ids.All(id => id is not null) && ids.Distinct().Count() == 1, "Hosts created overlapping executions.");
        await Task.WhenAll(store.Maintain(100, ct), second.Maintain(100, ct));
        Check((await store.List(cancellationToken: ct)).Count == 1, "Maintenance bypassed manual-run coalescing.");
        JobLease lease = (await store.Claim("first", TimeSpan.FromSeconds(30), ct))!;
        await store.Finish(lease, JobOutcome.Failed, "retry", TimeSpan.FromMinutes(1), ct);
        Check(await second.RunRecurring("scan", ct) == ids[0], "Retry released the outstanding occurrence.");
        await second.Cancel(ids[0]!, ct);
        string? next = await store.RunRecurring("scan", ct);
        Check(next is not null && next != ids[0], "Terminal occurrence was not released.");
        Check((await second.GetRecurringDefinition("scan", ct))!.Definition.CoalesceOccurrences,
            "Coalescing policy did not persist across hosts.");
    }));

    [Test]
    public ValueTask RenewalCannotOverwriteCancellationOrCompletionAcrossHosts(CancellationToken ct) => new(WithStore(async (store, db, ns) =>
    {
        await using var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        for (int i = 0; i < 10; i++)
        {
            string id = await store.Enqueue(Request(), ct);
            JobLease lease = (await store.Claim("worker", TimeSpan.FromSeconds(30), ct))!;
            await Task.WhenAll(store.Renew(lease, TimeSpan.FromSeconds(30), ct), other.Cancel(id, ct));
            Check((await store.Get(id, ct))!.CancelRequested, "A point update lost concurrent cancellation.");
            await Task.WhenAll(store.Renew(lease, TimeSpan.FromSeconds(30), ct),
                other.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, ct));
            Check((await store.Get(id, ct))!.State == JobState.Cancelled, "Renewal resurrected a terminal job.");
            Check(await store.Renew(lease, TimeSpan.FromSeconds(30), ct) == LeaseStatus.Lost, "Terminal owner renewed a lease.");
        }
    }));

    [Test]
    public ValueTask DefaultPolicyRecoversCrashWithoutGrantingFailureRetries(CancellationToken ct) => new(WithStore(async store =>
    {
        string id = await store.Enqueue(Request() with { Policy = new JobPolicy { InitialBackoff = TimeSpan.FromMilliseconds(1), Jitter = 0 } }, ct);
        JobLease old = (await store.Claim("crashed", TimeSpan.FromMilliseconds(50), ct))!;
        await Task.Delay(100, ct);
        await store.Maintain(100, ct);
        await Task.Delay(10, ct);
        JobLease recovered = (await store.Claim("replacement", TimeSpan.FromSeconds(30), ct))!;
        Check(recovered.Job.Id == id && recovered.Job.StaleRestartCount == 1 && recovered.Job.FailureCount == 0,
            "Crash recovery consumed the failure budget.");
        Check(!await store.Finish(old, JobOutcome.Succeeded, null, TimeSpan.Zero, ct), "Old owner was not fenced.");
        await store.Finish(recovered, JobOutcome.Failed, "failure", TimeSpan.Zero, ct);
        JobRecord result = (await store.Get(id, ct))!;
        Check(result.State == JobState.DeadLettered && result.FailureCount == 1 && result.StaleRestartCount == 1,
            "Default failure policy retried after crash recovery.");
    }));
}
