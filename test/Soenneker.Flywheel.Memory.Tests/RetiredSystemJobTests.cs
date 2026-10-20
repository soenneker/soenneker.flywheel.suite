using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Memory;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class RetiredSystemJobTests
{
    [Test]
    public async Task MaintenanceRetiresExporterWithoutRemovingUserJobsOrLogs(CancellationToken cancellationToken)
    {
        const string name = "Flywheel.LogArchive.Export";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlywheel().AddMemory();
        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<MemoryJobStore>();
        var database = provider.GetRequiredKeyedService<MemoryLibrarianDatabase>(MemoryRegistration.LibrarianServiceKey);
        var request = new EnqueueRequest(name, "{}", new(), TimeSpan.Zero);
        await store.AddRecurring(name, request, TimeSpan.FromSeconds(10), cancellationToken);
        string retiredId = await store.RunRecurring(name, cancellationToken) ?? throw new Exception("Missing schedule.");
        string userId = await store.Enqueue(request, cancellationToken);

        // Seed the persisted system flags written by the old exporter registration.
        foreach (var item in new[] { (Container: "flywheel.schedules", Key: name), (Container: "flywheel.jobs", Key: retiredId) })
        {
            string id = new Soenneker.Hashing.Sha256.Sha256HashingUtil().Hash(System.Text.Json.JsonSerializer.Serialize(item.Key));
            var container = await database.GetContainer(item.Container, cancellationToken);
            var document = JsonNode.Parse((await container.GetItem(id, cancellationToken))!)!;
            JsonNode value = document["value"]!;
            if (item.Container == "flywheel.schedules") value = value["job"]!;
            value["isSystem"] = true;
            if (!await database.Execute(new LibrarianBatch([new LibrarianWrite(item.Container, id, document.ToJsonString())]), cancellationToken))
                throw new Exception("Could not seed legacy system job.");
        }

        await store.Maintain(100, cancellationToken);
        await store.Maintain(100, cancellationToken);
        if ((await store.ListRecurring(cancellationToken: cancellationToken)).Count != 0 ||
            (await store.Get(retiredId, cancellationToken))!.State != JobState.Cancelled)
            throw new Exception("Retired exporter remains active.");
        var lease = await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken);
        if (lease?.Job.Id != userId) throw new Exception("User job was removed with the exporter.");
        await store.AppendLogs(lease, [new("Information", "test", "Still captured")], cancellationToken);
        if ((await store.GetLogs(userId, cancellationToken: cancellationToken)).Single().Message != "Still captured")
            throw new Exception("Normal log capture was affected.");
    }
}
