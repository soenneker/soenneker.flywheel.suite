using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Responses;
using System.Threading;
using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask ServerHeartbeatsReportWorkerCapacity(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        await store.Heartbeat("server-one", 12, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        await store.Heartbeat("server-two", 4, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

        IReadOnlyList<WorkerServerView> servers = await store.ListServers(cancellationToken: cancellationToken);
        Check(servers.Sum(server => server.Workers) == 16, "Total live worker capacity was incorrect");
        Check(await store.GetTotalWorkerCount(cancellationToken: cancellationToken) == 16, "Worker capacity aggregate was incorrect");
        Check((await store.GetServer("server-one", cancellationToken: cancellationToken))?.Workers == 12, "Server worker capacity was not persisted");
    }));

    [Test]
    public ValueTask HeartbeatDoesNotInvalidateDispatchOrNotifyUnchangedCapacity(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<JobChange> feed = store.Watch(timeout.Token).GetAsyncEnumerator(cancellationToken: cancellationToken);
        Check(await feed.MoveNextAsync() && feed.Current.Kind == "Resync", "Initial resync missing");
        await store.Heartbeat("node", 12, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        Check(await feed.MoveNextAsync() && feed.Current.Kind == "Servers", "New server notification missing");
        string? revision = await ControlValue(db, ns, "revision");
        Task<bool> next = feed.MoveNextAsync().AsTask();
        await store.Heartbeat("node", 12, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        await Task.Delay(100, cancellationToken: cancellationToken);
        Check(!next.IsCompleted, "Unchanged heartbeat triggered a dashboard refresh");
        Check(await ControlValue(db, ns, "revision") == revision, "Heartbeat invalidated job selection");
        await store.Heartbeat("node", 8, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        Check(await next && feed.Current.Kind == "Servers", "Capacity change notification missing");
    }));

    [Test]
    public ValueTask ReturningServerKeepsCapacityWhileExpiredPeersAreRemoved(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        await store.Heartbeat("returning", 8, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        await store.Heartbeat("expired", 3, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        await SeedRecord(db, ns, "nodes", "returning", new { expiresAt = 0L, workers = 8 });
        await SeedRecord(db, ns, "nodes", "expired", new { expiresAt = 0L, workers = 3 });
        await store.Heartbeat("returning", 12, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        Check(await store.GetTotalWorkerCount(cancellationToken: cancellationToken) == 12, "Returning server lost its worker count");
        await using var database = OpenLibrarian(db, ns);
        Check(await (await database.GetContainer("flywheel.nodes", cancellationToken: cancellationToken)).GetItem(DocumentId("expired"), cancellationToken: cancellationToken) is null, "Expired peer capacity was retained");
        Check((await store.ListServers(cancellationToken: cancellationToken)).Count == 1, "Expired peer remained live");
    }));
}
