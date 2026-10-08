using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using System.Threading;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask DispatchIncludesLegacyEntriesDuringRollingUpgrade(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        string modern = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Low } }, cancellationToken: cancellationToken);
        string legacy = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Critical } }, cancellationToken: cancellationToken);
        await using var database = OpenLibrarian(db, ns);
        var dispatch = await database.GetContainer("flywheel.dispatch", cancellationToken: cancellationToken);
        await dispatch.EnsureIndex("value.order", cancellationToken: cancellationToken);
        string key = DocumentId(legacy);
        var document = System.Text.Json.Nodes.JsonNode.Parse((await dispatch.GetItem(key, cancellationToken: cancellationToken))!)!;
        document["value"]!.AsObject().Remove("order");
        await dispatch.UpdateItemStrict(key, document.ToJsonString(), cancellationToken: cancellationToken);
        Check((await store.Claim("legacy-first", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!.Job.Id == legacy,
            "The derived index hid a legacy high-priority entry.");
        Check((await store.Claim("modern-next", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!.Job.Id == modern,
            "Dispatch did not return to the bounded path after legacy work was claimed.");
    }));

    [Test]
    public ValueTask DispatchPreservesPriorityAndVersionAcrossBatches(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        for (int i = 0; i < 140; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Low } }, cancellationToken: cancellationToken);
        string restricted = await store.EnqueueForCurrentInstance(Request() with
        {
            Policy = new JobPolicy { Priority = JobPriority.Critical }
        }, "build-2", "versioned", cancellationToken: cancellationToken);
        JobLease ordinary = (await store.Claim("unversioned", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        Check(ordinary.Job.Id != restricted && ordinary.Job.Policy.Priority == JobPriority.Low,
            "Unversioned worker executed a restricted job");
        Check((await store.ClaimForVersion("versioned", TimeSpan.FromMinutes(1), "build-2", cancellationToken: cancellationToken))!.Job.Id == restricted,
            "Dispatch lost a high priority job beyond the first batch");
    }));

    [Test]
    public ValueTask MissingDispatchMetadataFailsInsteadOfSilentlySkippingWork(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        await using var database = OpenLibrarian(db, ns);
        await (await database.GetContainer("flywheel.dispatch", cancellationToken: cancellationToken)).DeleteAllItems(cancellationToken: cancellationToken);
        try
        {
            await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
        }
        catch (System.IO.InvalidDataException ex) when (ex.Message.StartsWith("Required dispatch metadata is missing", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Missing dispatch metadata was silently accepted");
    }));

    [Test]
    public ValueTask DispatchIndexSupportsConcurrentClaims(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        for (int i = 0; i < 30; i++) await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease?[] leases = await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            new RedisJobStore(_ => Task.FromResult(db), ns).Claim("worker-" + i, TimeSpan.FromMinutes(1), cancellationToken: cancellationToken)));
        Check(leases.All(l => l is not null) && leases.Select(l => l!.Job.Id).Distinct().Count() == 30,
            "Concurrent dispatch lost work or admitted duplicate leases");
        Check(await store.Claim("empty", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is null, "Claimed an already running job");
    }));
    [Test]
    public ValueTask DispatchPreservesPriorityAcrossPipelineWindows(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        for (int i = 0; i < 530; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Low } }, cancellationToken: cancellationToken);
        string high = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Critical } }, cancellationToken: cancellationToken);
        Check((await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!.Job.Id == high,
            "Dispatch missed the highest priority job after a pipeline window");
    }));

    [Test]
    public ValueTask DispatchMetadataExcludesPayloadAndIsRemovedOnCompletion(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        string payload = JsonSerializer.Serialize(new { Secret = new string('x', 16000) });
        string id = await store.EnqueueForCurrentInstance(Request() with { Name = "work-\"\\日本語-🚀", Payload = payload }, "build-β-🚀", "worker", cancellationToken: cancellationToken);
        await using var database = OpenLibrarian(db, ns);
        var dispatch = await database.GetContainer("flywheel.dispatch", cancellationToken: cancellationToken);
        string? metadata = await dispatch.GetItem(DocumentId(id), cancellationToken: cancellationToken);
        Check(metadata is not null && metadata.Length < 1000 && !metadata.Contains(new string('x', 100)),
            "Dispatch index contains a job payload");
        Check(await store.Claim("unversioned", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is null, "Binary metadata lost a version restriction");
        JobLease lease = (await store.ClaimForVersion("worker", TimeSpan.FromMinutes(1), "build-β-🚀", cancellationToken: cancellationToken))!;
        Check(lease.Job.Payload == payload && lease.Job.Name == "work-\"\\日本語-🚀", "Claim did not preserve UTF-8 metadata and payload");
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Completion failed");
        Check(await dispatch.GetItem(DocumentId(id), cancellationToken: cancellationToken) is null, "Completed job leaked dispatch metadata");
    }));
}
