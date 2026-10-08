using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Generated;
using System.Threading;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask PayloadSerializationPreservesJsonNullForEnqueuesAndChains(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        var services = new ServiceCollection();
        services.AddSingleton<System.Text.Json.Serialization.JsonSerializerContext>(TestJsonContext.Default);
        services.AddLogging();
        services.AddFlywheel().AddGeneratedJobs();
        services.AddSingleton<IJobStore>(store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IJobClient>();
        string id = await client.Enqueue(FlywheelJobs.IntegrationJobs_Run, (TestPayload)null!, cancellationToken: cancellationToken);
        Check((await store.Get(id, cancellationToken: cancellationToken))!.Payload == "null", "Null enqueue payload did not produce valid JSON");
        IReadOnlyList<string> chain = await client.Chain([FlywheelJobs.IntegrationJobs_Run.With(null!, TestJsonContext.Get<TestPayload>())], cancellationToken: cancellationToken);
        Check((await store.Get(chain[0], cancellationToken: cancellationToken))!.Payload == "null", "Null chain payload did not produce valid JSON");
        string valueId = await client.Enqueue(FlywheelJobs.IntegrationJobs_Run, new TestPayload("camelCase"), cancellationToken: cancellationToken);
        JsonNode payload = JsonNode.Parse((await store.Get(valueId, cancellationToken: cancellationToken))!.Payload)!;
        Check(payload["value"]!.GetValue<string>() == "camelCase", "Typed payload did not use camelCase");
    }));

    [Test]
    public ValueTask CamelCaseStoragePreservesOwnership(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 1, RateLimit = 2 }, cancellationToken: cancellationToken);
        string id = await store.EnqueueForCurrentInstance(Request(), "release-2", "worker", cancellationToken: cancellationToken);
        await store.AddRecurring("schedule", Request() with { Name = "recurring.v1" }, TimeSpan.FromHours(1), cancellationToken: cancellationToken);
        JobLease lease = (await store.ClaimForVersion("worker", TimeSpan.FromSeconds(30), "release-2", cancellationToken: cancellationToken))!;
        await using var database = OpenLibrarian(db, ns);
        foreach (string table in new[] { "jobs", "schedules", "policies", "rates" })
        {
            var entries = await (await database.GetContainer("flywheel." + table, cancellationToken: cancellationToken)).GetAllItems(cancellationToken: cancellationToken);
            Check(entries.Count > 0, "Missing persisted fixture: " + table);
            foreach (string entry in entries) VerifyCamelCase(JsonNode.Parse(entry));
        }

        JobRecord restored = (await store.Get(id, cancellationToken: cancellationToken))!;
        Check(restored.ApplicationVersion == "release-2" && restored.Policy.MaxAttempts == 3 && restored.Owner == "worker",
            "Stored job fields were lost");
        Check(await store.Renew(lease, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) == LeaseStatus.Renewed, "Stored permit did not renew");
        Check(await store.Finish(lease, JobOutcome.Failed, "retry", TimeSpan.Zero, cancellationToken: cancellationToken), "Stored lease did not finish");
        Check(await store.ClaimForVersion("old", TimeSpan.FromSeconds(30), "release-1", cancellationToken: cancellationToken) is null,
            "Stored job lost version restriction");
        Check(await store.ClaimForVersion("new", TimeSpan.FromSeconds(30), "release-2", cancellationToken: cancellationToken) is null, "Peer claimed instance retry");
        JobLease retry = (await store.ClaimForVersion("worker", TimeSpan.FromSeconds(30), "release-2", cancellationToken: cancellationToken))!;
        Check(retry.Job.Id == id && retry.Job.Attempt == 2, "Stored policies or rate window blocked the retry");
        await store.Finish(retry, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
        await store.Maintain(100, cancellationToken: cancellationToken);
        JobLease recurring = (await store.Claim("worker", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check(recurring.Job.Name == "recurring.v1", "Stored schedule did not materialize");
    }));

    private static void VerifyCamelCase(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (KeyValuePair<string, JsonNode?> property in obj)
            {
                Check(char.IsLower(property.Key[0]), "Non-camelCase persisted property: " + property.Key);
                VerifyCamelCase(property.Value);
            }
        else if (node is JsonArray array)
            foreach (JsonNode? value in array) VerifyCamelCase(value);
    }
}
