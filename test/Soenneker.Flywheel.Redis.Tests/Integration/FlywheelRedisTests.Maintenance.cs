using System;
using System.Threading.Tasks;
using System.Threading;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask RecoveryPrunesNewTerminalJobsInTheSamePassWhenRetentionIsDisabled(CancellationToken cancellationToken) => new ValueTask(WithStore(async (_, db, ns) =>
    {
        var store = new RedisJobStore(_ => Task.FromResult(db), ns, retainCompletedJobs: false);
        string id = await store.Enqueue(Request(attempts: 1), cancellationToken: cancellationToken);
        Check(await store.Claim("expiring", TimeSpan.FromMilliseconds(50), cancellationToken: cancellationToken) is not null, "Claim failed");
        await Task.Delay(100, cancellationToken: cancellationToken);
        await store.Maintain(100, cancellationToken: cancellationToken);
        Check(await store.Get(id, cancellationToken: cancellationToken) is null, "Recovery retained a terminal job despite immediate cleanup");
    }));
}
