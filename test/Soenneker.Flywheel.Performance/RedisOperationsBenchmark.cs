using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis.Performance;

public static class RedisOperationsBenchmark
{
    public static async Task Run()
    {
        // Process-wide allocations include Redis client work on background threads. Run against an isolated idle server.
        using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("FLYWHEEL_TEST_REDIS") ?? "localhost:16379");
        IDatabase db = connection.GetDatabase();
        IServer server = connection.GetServer((await db.IdentifyEndpointAsync("performance"))!);
        var results = new List<object>();
        string payload = JsonSerializer.Serialize(new { Value = new string('x', 4096) });
        EnqueueRequest Request(string name = "work") => new(name, payload, new JobPolicy(), TimeSpan.Zero, null);

        await Scenario("idle", async (store, measure) => await measure(() => store.Claim("idle", TimeSpan.FromMinutes(10)), 100));
        await Scenario("idle-maintenance", async (store, measure) => await measure(() => store.Maintain(100), 100));
        await Scenario("idle-recorder", async (store, measure) =>
        {
            var record = (Func<System.Threading.CancellationToken, Task<bool>>)typeof(RedisJobStore)
                .GetMethod("SampleLiveActivity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .CreateDelegate(typeof(Func<System.Threading.CancellationToken, Task<bool>>), store);
            await measure(() => record(default), 100);
        });
        await Scenario("backlog-1000", async (store, measure) =>
        {
            for (int i = 0; i < 1000; i++) await store.Enqueue(Request());
            await measure(async () =>
            {
                JobLease lease = (await store.Claim("backlog", TimeSpan.FromMinutes(10)))!;
                if (!await store.Finish(lease, JobOutcome.Failed, null, TimeSpan.Zero)) throw new Exception("Completion failed");
            }, 100);
        });
        await Scenario("renew", async (store, measure) =>
        {
            await store.ConfigureMethod("work", new MethodPolicy { MaxConcurrency = 1 });
            await store.Enqueue(Request());
            JobLease lease = (await store.Claim("renew", TimeSpan.FromMinutes(10)))!;
            await measure(async () =>
            {
                if (await store.Renew(lease, TimeSpan.FromMinutes(10)) != LeaseStatus.Renewed) throw new Exception("Renew failed");
            }, 100);
        });
        await Scenario("concurrency-100", async (store, measure) =>
        {
            await store.ConfigureMethod("work", new MethodPolicy { MaxConcurrency = 101 });
            for (int i = 0; i < 101; i++) await store.Enqueue(Request());
            for (int i = 0; i < 100; i++)
                if (await store.Claim("running-" + i, TimeSpan.FromMinutes(10)) is null)
                    throw new Exception("Could not populate running jobs");
            // Keep exactly one pending job throughout warmup and measurement.
            await measure(async () =>
            {
                JobLease lease = (await store.Claim("concurrency", TimeSpan.FromMinutes(10)))!;
                if (!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero)) throw new Exception("Completion failed");
                await store.Enqueue(Request());
            }, 100);
        });
        await Scenario("logs-50", async (store, measure) =>
        {
            await store.Enqueue(Request());
            JobLease lease = (await store.Claim("logs", TimeSpan.FromMinutes(10)))!;
            JobLogMessage[] messages = Enumerable.Range(0, 50).Select(i => new Soenneker.Flywheel.Communication.Logging.Dtos.JobLogMessage("Information", "performance", "message " + i)).ToArray();
            await measure(async () => { if (!await store.AppendLogs(lease, messages)) throw new Exception("Log append failed"); }, 50);
        });
        string json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        string? outputPath = Environment.GetEnvironmentVariable("FLYWHEEL_BENCHMARK_OUTPUT");
        if (!string.IsNullOrWhiteSpace(outputPath)) await File.WriteAllTextAsync(outputPath, json);

        async Task Scenario(string name, Func<RedisJobStore, Func<Func<Task>, int, Task>, Task> run)
        {
            string ns = "performance-" + Guid.NewGuid().ToString("N");
            string prefix = "flywheel:{" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ns))) + "}:v1:";
            var store = new RedisJobStore(_ => Task.FromResult(db), ns);
            try
            {
                await run(store, async (operation, iterations) =>
                {
                    const int warmupIterations = 50;
                    for (int i = 0; i < warmupIterations; i++) await operation();
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    using Process process = Process.GetCurrentProcess();
                    double redisCpu = await RedisCpu();
                    long commands = await Commands();
                    TimeSpan processCpu = process.TotalProcessorTime;
                    long allocated = GC.GetTotalAllocatedBytes(true);
                    long started = Stopwatch.GetTimestamp();
                    for (int i = 0; i < iterations; i++) await operation();
                    double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                    double cpuMilliseconds = (process.TotalProcessorTime - processCpu).TotalMilliseconds;
                    long commandCount = await Commands() - commands - 1; // Exclude the ending INFO.
                    double redisCpuMilliseconds = (await RedisCpu() - redisCpu) * 1000;
                    results.Add(new { Scenario = name, WarmupIterations = warmupIterations, Iterations = iterations, MillisecondsPerOperation = elapsed / iterations,
                        BytesPerOperation = bytes / iterations, RedisCommandsPerOperation = (double)commandCount / iterations,
                        ProcessCpuMillisecondsPerOperation = cpuMilliseconds / iterations,
                        RedisCpuMillisecondsPerOperation = redisCpuMilliseconds / iterations });
                });
            }
            finally
            {
                await foreach (RedisKey key in server.KeysAsync(pattern: prefix + "*")) await db.KeyDeleteAsync(key);
            }
        }
        async Task<long> Commands()
        {
            IGrouping<string, KeyValuePair<string, string>>[] info = await server.InfoAsync("stats");
            return long.Parse(info.SelectMany(group => group).Single(pair => pair.Key == "total_commands_processed").Value);
        }
        async Task<double> RedisCpu()
        {
            IGrouping<string, KeyValuePair<string, string>>[] info = await server.InfoAsync("cpu");
            return info.SelectMany(group => group).Where(pair => pair.Key is "used_cpu_user" or "used_cpu_sys")
                .Sum(pair => double.Parse(pair.Value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
