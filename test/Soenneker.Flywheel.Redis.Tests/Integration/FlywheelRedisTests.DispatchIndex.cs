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
    public Task DispatchPreservesPriorityAndVersionAcrossBatches() => WithStore(async store =>
    {
        for (int i = 0; i < 140; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Low } });
        string restricted = await store.EnqueueForCurrentVersion(Request() with
        {
            Policy = new JobPolicy { Priority = JobPriority.Critical }
        }, "build-2");
        JobLease ordinary = (await store.Claim("unversioned", TimeSpan.FromMinutes(1)))!;
        Check(ordinary.Job.Id != restricted && ordinary.Job.Policy.Priority == JobPriority.Low,
            "Unversioned worker executed a restricted job");
        Check((await store.ClaimForVersion("versioned", TimeSpan.FromMinutes(1), "build-2"))!.Job.Id == restricted,
            "Dispatch lost a high priority job beyond the first batch");
    });

    [Test]
    public Task MissingDispatchMetadataFailsInsteadOfSilentlySkippingWork() => WithStore(async (store, db, ns) =>
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
    });

    [Test]
    public Task DispatchIndexSupportsConcurrentClaims() => WithStore(async (store, db, ns) =>
    {
        for (int i = 0; i < 30; i++) await store.Enqueue(Request());
        JobLease?[] leases = await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            new RedisJobStore(_ => Task.FromResult(db), ns).Claim("worker-" + i, TimeSpan.FromMinutes(1))));
        Check(leases.All(l => l is not null) && leases.Select(l => l!.Job.Id).Distinct().Count() == 30,
            "Concurrent dispatch lost work or admitted duplicate leases");
        Check(await store.Claim("empty", TimeSpan.FromMinutes(1)) is null, "Claimed an already running job");
    });
    [Test]
    public Task DispatchPreservesPriorityAcrossPipelineWindows() => WithStore(async (store, db, ns) =>
    {
        for (int i = 0; i < 530; i++)
            await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Low } });
        string high = await store.Enqueue(Request() with { Policy = new JobPolicy { Priority = JobPriority.Critical } });
        Check((await store.Claim("worker", TimeSpan.FromMinutes(1)))!.Job.Id == high,
            "Dispatch missed the highest priority job after a pipeline window");
    });

    [Test]
    public Task DispatchMetadataExcludesPayloadAndIsRemovedOnCompletion() => WithStore(async (store, db, ns) =>
    {
        string payload = JsonSerializer.Serialize(new { Secret = new string('x', 16000) });
        string id = await store.EnqueueForCurrentVersion(Request() with { Name = "work-\"\\日本語-🚀", Payload = payload }, "build-β-🚀");
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
    });
}
