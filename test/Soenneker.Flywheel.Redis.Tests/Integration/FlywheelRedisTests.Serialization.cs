using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Utils.Json;
using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Generated;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task PayloadSerializationPreservesJsonNullForEnqueuesAndChains() => WithStore(async store =>
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlywheel().AddGeneratedJobs();
        services.AddSingleton<IJobStore>(store);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IJobClient>();
        string id = await client.Enqueue(FlywheelJobs.IntegrationJobs_Run, (TestPayload)null!);
        Check((await store.Get(id))!.Payload == "null", "Null enqueue payload did not produce valid JSON");
        var chain = await client.Chain([FlywheelJobs.IntegrationJobs_Run.With(null!)]);
        Check((await store.Get(chain[0]))!.Payload == "null", "Null chain payload did not produce valid JSON");
        string valueId = await client.Enqueue(FlywheelJobs.IntegrationJobs_Run, new TestPayload("camelCase"));
        JsonNode payload = JsonNode.Parse((await store.Get(valueId))!.Payload)!;
        Check(payload["value"]!.GetValue<string>() == "camelCase", "Typed payload did not use camelCase");
    });

    [Test]
    public Task CamelCaseStoragePreservesOwnership() => WithStore(async (store, db, ns) =>
    {
        await store.ConfigureMethod("test.v1", new MethodPolicy { MaxConcurrency = 1, RateLimit = 2 });
        string id = await store.RunOnceForCurrentVersion(Request(), "release-2");
        await store.AddRecurring("schedule", Request() with { Name = "recurring.v1" }, TimeSpan.FromHours(1));
        JobLease lease = (await store.ClaimForVersion("worker", TimeSpan.FromSeconds(30), "release-2"))!;
        string prefix = $"flywheel:{{{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ns)))}}}:v1:";
        foreach (string hash in new[] { "jobs", "schedules", "function-policies", "function-rates", "job-permits" })
        {
            HashEntry[] entries = await db.HashGetAllAsync(prefix + hash);
            Check(entries.Length > 0, "Missing persisted fixture: " + hash);
            foreach (HashEntry entry in entries)
            {
                JsonNode json = JsonNode.Parse((string)entry.Value!)!;
                VerifyCamelCase(json);
            }
        }

        JobRecord restored = (await store.Get(id))!;
        Check(restored.ApplicationVersion == "release-2" && restored.Policy.MaxAttempts == 3 && restored.Owner == "worker",
            "Stored job fields were lost");
        Check(await store.Renew(lease, TimeSpan.FromSeconds(30)) == LeaseStatus.Renewed, "Stored permit did not renew");
        Check(await store.Finish(lease, JobOutcome.Failed, "retry", TimeSpan.Zero), "Stored lease did not finish");
        Check(await store.ClaimForVersion("old", TimeSpan.FromSeconds(30), "release-1") is null,
            "Stored job lost version restriction");
        JobLease retry = (await store.ClaimForVersion("new", TimeSpan.FromSeconds(30), "release-2"))!;
        Check(retry.Job.Id == id && retry.Job.Attempt == 2, "Stored policies or rate window blocked the retry");
        await store.Finish(retry, JobOutcome.Succeeded, null, TimeSpan.Zero);
        await store.Maintain(100);
        JobLease recurring = (await store.Claim("worker", TimeSpan.FromSeconds(30)))!;
        Check(recurring.Job.Name == "recurring.v1", "Stored schedule did not materialize");
    });

    private static void VerifyCamelCase(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var property in obj)
            {
                Check(char.IsLower(property.Key[0]), "Non-camelCase persisted property: " + property.Key);
                VerifyCamelCase(property.Value);
            }
        else if (node is JsonArray array)
            foreach (JsonNode? value in array) VerifyCamelCase(value);
    }
}
