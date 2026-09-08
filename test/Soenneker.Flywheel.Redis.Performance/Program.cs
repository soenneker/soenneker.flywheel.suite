using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Soenneker.Flywheel.Core.Enums;
using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Requests;
using Soenneker.Flywheel.Redis;
using StackExchange.Redis;

// Process-wide allocations include Redis client work on background threads. Run against an isolated idle server.
using var connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("FLYWHEEL_TEST_REDIS") ?? "localhost:16379");
var db = connection.GetDatabase();
var server = connection.GetServer((await db.IdentifyEndpointAsync("performance"))!);
var results = new List<object>();
string payload = JsonSerializer.Serialize(new { Value = new string('x', 4096) });
EnqueueRequest Request(string name = "work") => new(name, payload, new JobPolicy(), TimeSpan.Zero, null);

await Scenario("idle", async (store, measure) => await measure(() => store.Claim("idle", TimeSpan.FromMinutes(10)), 100));
await Scenario("backlog-1000", async (store, measure) =>
{
    for (int i = 0; i < 1000; i++) await store.Enqueue(Request());
    await measure(async () =>
    {
        var lease = (await store.Claim("backlog", TimeSpan.FromMinutes(10)))!;
        if (!await store.Finish(lease, JobOutcome.Failed, null, TimeSpan.Zero)) throw new Exception("Completion failed");
    }, 30);
});
await Scenario("renew", async (store, measure) =>
{
    await store.ConfigureMethod("work", new MethodPolicy { MaxConcurrency = 1 });
    await store.Enqueue(Request());
    var lease = (await store.Claim("renew", TimeSpan.FromMinutes(10)))!;
    await measure(async () =>
    {
        if (await store.Renew(lease, TimeSpan.FromMinutes(10)) != LeaseStatus.Renewed) throw new Exception("Renew failed");
    }, 100);
});
await Scenario("logs-50", async (store, measure) =>
{
    await store.Enqueue(Request());
    var lease = (await store.Claim("logs", TimeSpan.FromMinutes(10)))!;
    var messages = Enumerable.Range(0, 50).Select(i => new Soenneker.Flywheel.Core.Logging.Dtos.JobLogMessage("Information", "performance", "message " + i)).ToArray();
    await measure(async () => { if (!await store.AppendLogs(lease, messages)) throw new Exception("Log append failed"); }, 50);
});
var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
if (args.Length > 0) await File.WriteAllTextAsync(args[0], json);

async Task Scenario(string name, Func<RedisJobStore, Func<Func<Task>, int, Task>, Task> run)
{
    string ns = "performance-" + Guid.NewGuid().ToString("N");
    string prefix = "flywheel:{" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ns))) + "}:v1:";
    var store = new RedisJobStore(_ => Task.FromResult(db), ns);
    try
    {
        await run(store, async (operation, iterations) =>
        {
            for (int i = 0; i < 5; i++) await operation();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long commands = await Commands();
            long allocated = GC.GetTotalAllocatedBytes(true);
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) await operation();
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
            long commandCount = await Commands() - commands - 1; // Exclude the ending INFO.
            results.Add(new { Scenario = name, Iterations = iterations, MillisecondsPerOperation = elapsed / iterations,
                BytesPerOperation = bytes / iterations, RedisCommandsPerOperation = (double)commandCount / iterations });
        });
    }
    finally
    {
        await foreach (var key in server.KeysAsync(pattern: prefix + "*")) await db.KeyDeleteAsync(key);
    }
}
async Task<long> Commands()
{
    var info = await server.InfoAsync("stats");
    return long.Parse(info.SelectMany(group => group).Single(pair => pair.Key == "total_commands_processed").Value);
}
