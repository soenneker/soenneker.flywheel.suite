using System;
using System.Linq;
using System.Threading.Tasks;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task ServerHeartbeatsReportWorkerCapacity() => WithStore(async store =>
    {
        await store.Heartbeat("server-one", 12, TimeSpan.FromSeconds(30));
        await store.Heartbeat("server-two", 4, TimeSpan.FromSeconds(30));

        var servers = await store.ListServers();
        Check(servers.Sum(server => server.Workers) == 16, "Total live worker capacity was incorrect");
        Check(await store.GetTotalWorkerCount() == 16, "Worker capacity aggregate was incorrect");
        Check((await store.GetServer("server-one"))?.Workers == 12, "Server worker capacity was not persisted");
    });
}
