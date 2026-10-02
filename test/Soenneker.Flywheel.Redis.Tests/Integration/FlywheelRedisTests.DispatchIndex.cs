using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask DispatchIncludesLegacyEntriesDuringRollingUpgrade() => new ValueTask(WithStore(async (store, db, ns) =>
    {
        string modern = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Low } });
        string legacy = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Critical } });
        await using var database = OpenLibrarian(db, ns);
        var dispatch = await database.GetContainer("flywheel.dispatch");
        await dispatch.EnsureIndex("value.order");
        string key = DocumentId(legacy);
        var document = System.Text.Json.Nodes.JsonNode.Parse((await dispatch.GetItem(key))!)!;
        document["value"]!.AsObject().Remove("order");
        await dispatch.UpdateItemStrict(key, document.ToJsonString());
        Check((await store.Claim("legacy-first", TimeSpan.FromMinutes(1)))!.Job.Id == legacy,
            "The derived index hid a legacy high-priority entry.");
        Check((await store.Claim("modern-next", TimeSpan.FromMinutes(1)))!.Job.Id == modern,
            "Dispatch did not return to the bounded path after legacy work was claimed.");
    }));

    [Test]
    public ValueTask DispatchPreservesPriorityAndVersionAcrossBatches() => new ValueTask(WithStore(async store =>
    {
        for (int i = 0; i < 140; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Low } });
        string restricted = await store.EnqueueForCurrentInstance(Request() with
        {
            Policy = new JobPolicy { Priority = JobPriority.Critical }
        }, "build-2", "versioned");
        JobLease ordinary = (await store.Claim("unversioned", TimeSpan.FromMinutes(1)))!;
        Check(ordinary.Job.Id != restricted && ordinary.Job.Policy.Priority == JobPriority.Low,
            "Unversioned worker executed a restricted job");
        Check((await store.ClaimForVersion("versioned", TimeSpan.FromMinutes(1), "build-2"))!.Job.Id == restricted,
            "Dispatch lost a high priority job beyond the first batch");
    }));

    [Test]
    public ValueTask MissingDispatchMetadataFailsInsteadOfSilentlySkippingWork() => new ValueTask(WithStore(async (store, db, ns) =>
    {
        await store.Enqueue(Request());
        await using var database = OpenLibrarian(db, ns);
        await (await database.GetContainer("flywheel.dispatch")).DeleteAllItems();
        try
        {
            await store.Claim("worker", TimeSpan.FromMinutes(1));
        }
        catch (System.IO.InvalidDataException ex) when (ex.Message.StartsWith("Required dispatch metadata is missing", StringComparison.Ordinal))
        {
            return;
        }
        throw new Exception("Missing dispatch metadata was silently accepted");
    }));

    [Test]
    public ValueTask DispatchIndexSupportsConcurrentClaims() => new ValueTask(WithStore(async (store, db, ns) =>
    {
        for (int i = 0; i < 30; i++) await store.Enqueue(Request());
        JobLease?[] leases = await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            new RedisJobStore(_ => Task.FromResult(db), ns).Claim("worker-" + i, TimeSpan.FromMinutes(1))));
        Check(leases.All(l => l is not null) && leases.Select(l => l!.Job.Id).Distinct().Count() == 30,
            "Concurrent dispatch lost work or admitted duplicate leases");
        Check(await store.Claim("empty", TimeSpan.FromMinutes(1)) is null, "Claimed an already running job");
    }));
    [Test]
    public ValueTask DispatchPreservesPriorityAcrossPipelineWindows() => new ValueTask(WithStore(async (store, db, ns) =>
    {
        for (int i = 0; i < 530; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Low } });
        string high = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Critical } });
        Check((await store.Claim("worker", TimeSpan.FromMinutes(1)))!.Job.Id == high,
            "Dispatch missed the highest priority job after a pipeline window");
    }));

    [Test]
    public ValueTask DispatchMetadataExcludesPayloadAndIsRemovedOnCompletion() => new ValueTask(WithStore(async (store, db, ns) =>
    {
        string payload = JsonSerializer.Serialize(new { Secret = new string('x', 16000) });
        string id = await store.EnqueueForCurrentInstance(Request() with { Name = "work-\"\\日本語-🚀", Payload = payload }, "build-β-🚀", "worker");
        await using var database = OpenLibrarian(db, ns);
        var dispatch = await database.GetContainer("flywheel.dispatch");
        string? metadata = await dispatch.GetItem(DocumentId(id));
        Check(metadata is not null && metadata.Length < 1000 && !metadata.Contains(new string('x', 100)),
            "Dispatch index contains a job payload");
        Check(await store.Claim("unversioned", TimeSpan.FromMinutes(1)) is null, "Binary metadata lost a version restriction");
        JobLease lease = (await store.ClaimForVersion("worker", TimeSpan.FromMinutes(1), "build-β-🚀"))!;
        Check(lease.Job.Payload == payload && lease.Job.Name == "work-\"\\日本語-🚀", "Claim did not preserve UTF-8 metadata and payload");
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Completion failed");
        Check(await dispatch.GetItem(DocumentId(id)) is null, "Completed job leaked dispatch metadata");
    }));
}
