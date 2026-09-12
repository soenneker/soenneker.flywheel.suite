using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Responses;
using System.Threading;
using Soenneker.Flywheel.Communication.Dtos;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task ServerHeartbeatsReportWorkerCapacity() => WithStore(async store =>
    {
        await store.Heartbeat("server-one", 12, TimeSpan.FromSeconds(30));
        await store.Heartbeat("server-two", 4, TimeSpan.FromSeconds(30));

        IReadOnlyList<WorkerServerView> servers = await store.ListServers();
        Check(servers.Sum(server => server.Workers) == 16, "Total live worker capacity was incorrect");
        Check(await store.GetTotalWorkerCount() == 16, "Worker capacity aggregate was incorrect");
        Check((await store.GetServer("server-one"))?.Workers == 12, "Server worker capacity was not persisted");
    });

    [Test]
    public Task HeartbeatDoesNotInvalidateDispatchOrNotifyUnchangedCapacity() => WithStore(async (store, db, ns) =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<JobChange> feed = store.Watch(timeout.Token).GetAsyncEnumerator();
        Check(await feed.MoveNextAsync() && feed.Current.Kind == "Resync", "Initial resync missing");
        await store.Heartbeat("node", 12, TimeSpan.FromSeconds(30));
        Check(await feed.MoveNextAsync() && feed.Current.Kind == "Servers", "New server notification missing");
        string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        RedisKey revisionKey = $"flywheel:{{{tag}}}:v1:revision";
        RedisValue revision = await db.StringGetAsync(revisionKey);
        Task<bool> next = feed.MoveNextAsync().AsTask();
        await store.Heartbeat("node", 12, TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        Check(!next.IsCompleted, "Unchanged heartbeat triggered a dashboard refresh");
        Check(await db.StringGetAsync(revisionKey) == revision, "Heartbeat invalidated job selection");
        await store.Heartbeat("node", 8, TimeSpan.FromSeconds(30));
        Check(await next && feed.Current.Kind == "Servers", "Capacity change notification missing");
    });

    [Test]
    public Task ReturningServerKeepsCapacityWhileExpiredPeersAreRemoved() => WithStore(async (store, db, ns) =>
    {
        await store.Heartbeat("returning", 8, TimeSpan.FromSeconds(30));
        await store.Heartbeat("expired", 3, TimeSpan.FromSeconds(30));
        string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        string prefix = $"flywheel:{{{tag}}}:v1:";
        await db.SortedSetAddAsync(prefix + "nodes", [new SortedSetEntry("returning", 0), new SortedSetEntry("expired", 0)]);
        await store.Heartbeat("returning", 12, TimeSpan.FromSeconds(30));
        Check(await store.GetTotalWorkerCount() == 12, "Returning server lost its worker count");
        Check(!await db.HashExistsAsync(prefix + "node-workers", "expired"), "Expired peer capacity was retained");
        Check((await store.ListServers()).Count == 1, "Expired peer remained live");
    });
}
