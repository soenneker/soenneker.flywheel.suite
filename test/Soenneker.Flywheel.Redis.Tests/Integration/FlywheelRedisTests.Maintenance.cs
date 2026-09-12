using System;
using System.Threading.Tasks;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task RecoveryPrunesNewTerminalJobsInTheSamePassWhenRetentionIsDisabled() => WithStore(async (_, db, ns) =>
    {
        var store = new RedisJobStore(_ => Task.FromResult(db), ns, retainCompletedJobs: false);
        string id = await store.Enqueue(Request(attempts: 1));
        Check(await store.Claim("expiring", TimeSpan.FromMilliseconds(50)) is not null, "Claim failed");
        await Task.Delay(100);
        await store.Maintain(100);
        Check(await store.Get(id) is null, "Recovery retained a terminal job despite immediate cleanup");
    });
}
