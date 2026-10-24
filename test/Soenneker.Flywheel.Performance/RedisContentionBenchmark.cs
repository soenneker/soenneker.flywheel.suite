using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Redis;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis.Performance;

public static class RedisContentionBenchmark
{
    public static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CancellationToken ct = deadline.Token;
        using ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable("FLYWHEEL_TEST_REDIS") ?? "localhost:6379");
        IDatabase db = connection.GetDatabase();
        string ns = "contention-" + Guid.NewGuid().ToString();
        var stores = Enumerable.Range(0, 4).Select(_ => new RedisJobStore(_ => Task.FromResult(db), ns)).ToArray();
        long conflicts = 0, timeouts = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Soenneker.Flywheel.Storage") meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "flywheel.storage.conflicts") Interlocked.Add(ref conflicts, value);
            if (instrument.Name == "flywheel.storage.timeouts") Interlocked.Add(ref timeouts, value);
        });
        listener.Start();
        var renewals = new ConcurrentBag<double>();
        var operations = new ConcurrentBag<double>();
        using var backgroundStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var background = new List<Task>();
        try
        {
            var leases = new JobLease[stores.Length];
            for (int i = 0; i < stores.Length; i++)
            {
                await stores[i].Enqueue(new EnqueueRequest("heartbeat", "{}", new JobPolicy(), TimeSpan.Zero), ct);
                leases[i] = (await stores[i].Claim("host-" + i, TimeSpan.FromSeconds(30), ct))!;
            }
            for (int i = 0; i < stores.Length; i++)
            {
                RedisJobStore host = stores[i];
                JobLease lease = leases[i];
                background.Add(Loop(TimeSpan.FromMilliseconds(100), async token =>
                {
                    long start = Stopwatch.GetTimestamp();
                    if (await host.Renew(lease, TimeSpan.FromSeconds(30), token) != LeaseStatus.Renewed)
                        throw new InvalidOperationException("A live lease was lost under contention.");
                    renewals.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }));
                background.Add(Loop(TimeSpan.FromMilliseconds(500), token => host.Maintain(20, token)));
            }
            long started = Stopwatch.GetTimestamp();
            await Task.WhenAll(stores.Select(async (host, index) =>
            {
                for (int i = 0; i < 30; i++)
                {
                    long start = Stopwatch.GetTimestamp();
                    await host.Enqueue(new EnqueueRequest("work", "{}", new JobPolicy(), TimeSpan.Zero), ct);
                    JobLease lease = await host.Claim("worker-" + index, TimeSpan.FromSeconds(30), ct)
                        ?? throw new InvalidOperationException("Queued work was not claimable.");
                    if (!await host.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, ct))
                        throw new InvalidOperationException("Completion lost ownership.");
                    operations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
            }));
            double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            await backgroundStop.CancelAsync();
            await Task.WhenAll(background);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Hosts = stores.Length, Completed = operations.Count, Seconds = elapsed,
                JobsPerSecond = operations.Count / elapsed, Conflicts = conflicts, Timeouts = timeouts,
                CycleP95Milliseconds = Percentile(operations, .95), RenewalCount = renewals.Count,
                RenewalP95Milliseconds = Percentile(renewals, .95), RenewalMaxMilliseconds = Percentile(renewals, 1)
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await backgroundStop.CancelAsync();
            try { await Task.WhenAll(background); }
            finally
            {
                foreach (var store in stores) await store.DisposeAsync();
                IServer server = connection.GetServer((await db.IdentifyEndpointAsync("flywheel:{" + ns + "}:containers:"))!);
                await foreach (RedisKey key in server.KeysAsync(pattern: "flywheel:{" + ns + "}:containers:*")) await db.KeyDeleteAsync(key);
            }
        }
        async Task Loop(TimeSpan interval, Func<CancellationToken, Task> action)
        {
            using var timer = new PeriodicTimer(interval);
            try { while (await timer.WaitForNextTickAsync(backgroundStop.Token)) await action(backgroundStop.Token); }
            catch (OperationCanceledException) when (backgroundStop.IsCancellationRequested) { }
        }
    }

    private static double Percentile(ConcurrentBag<double> measurements, double percentile)
    {
        double[] ordered = measurements.Order().ToArray();
        return ordered.Length == 0 ? 0 : ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1)];
    }
}
