using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Responses;
using StackExchange.Redis;
using System.Threading;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task LiveActivityExpiresAtTheEndOfItsVisibleWindow() => WithStore(async (store, db, ns) =>
    {
        await store.Enqueue(Request());
        using var recorder = new RedisLiveActivityRecorder(store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisLiveActivityRecorder>.Instance);
        await recorder.StartAsync(default);
        try
        {
            await Task.Delay(1200);
            string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
            var points = await store.GetLiveActivity();
            foreach (var point in points.Where(p => p.Scheduled > 0))
            {
                DateTime? expiry = await Expiry(db, $"flywheel:{{{tag}}}:v1:activity:{point.Timestamp / 1000}");
                Check(expiry == DateTimeOffset.FromUnixTimeMilliseconds(point.Timestamp + 61000).UtcDateTime,
                    "Live samples outlive their last visible second or their TTL was extended by another writer");
            }
            long latest = (long)await db.HashGetAsync($"flywheel:{{{tag}}}:v1:activity:samples", "latest");
            Check(await Expiry(db, $"flywheel:{{{tag}}}:v1:activity:samples") ==
                  DateTimeOffset.FromUnixTimeSeconds(latest + 61).UtcDateTime, "Sample ring did not expire with its final visible second");
        }
        finally { await recorder.StopAsync(default); }
    });

    [Test]
    public Task LiveSampleRingRejectsOverwrittenAndMalformedSeconds() => WithStore(async (store, db, ns) =>
    {
        string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        string prefix = $"flywheel:{{{tag}}}:v1:";
        long now = (await store.GetLiveActivity())[^1].Timestamp / 1000;
        long second = now - 10;
        await db.HashSetAsync(prefix + "activity:samples", second % 61, $"{second - 61}|99|99|99");
        await db.HashSetAsync(prefix + "activity:" + second,
            [new HashEntry(0, 2)]);
        JobHistoryPoint point = (await store.GetLiveActivity()).Single(p => p.Timestamp == second * 1000);
        Check(point.Scheduled == 2 && point.ScheduledCount is null && point.RunningCount is null && point.QueuedCount is null,
            "Stale ring slot produced a gauge sample or lost transition counts");
        await db.HashSetAsync(prefix + "activity:samples", second % 61, $"{second}|5|6|7");
        point = (await store.GetLiveActivity()).Single(p => p.Timestamp == second * 1000);
        Check(point.ScheduledCount == 5 && point.RunningCount == 6 && point.QueuedCount == 7, "Current ring slot was not read");
        await db.HashSetAsync(prefix + "activity:samples", second % 61, "invalid");
        point = (await store.GetLiveActivity()).Single(p => p.Timestamp == second * 1000);
        Check(point.RunningCount is null, "Malformed sample produced a gauge count");
    });

    [Test]
    public Task LiveSamplesAreSharedAcrossStoresWithoutChangingDispatchRevision() => WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        string prefix = $"flywheel:{{{tag}}}:v1:";
        await store.Enqueue(Request());
        RedisValue revision = await db.StringGetAsync(prefix + "revision");
        Func<CancellationToken, Task<bool>> first = Record(store), second = Record(other);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? first : second)(default)));
        Check(await db.StringGetAsync(prefix + "revision") == revision, "Sampler invalidated dispatch");
        long latest = (long)await db.HashGetAsync(prefix + "activity:samples", "latest");
        Check((string?)await db.HashGetAsync(prefix + "activity:samples", latest % 61) == $"{latest}|0|0|1",
            "Concurrent samplers produced an inconsistent count");
        Check(await db.HashLengthAsync(prefix + "activity:samples") <= 65, "Sample ring exceeded its fixed capacity");
        Check((await store.GetLiveActivity()).Sum(p => p.Scheduled) == 1, "Sampler changed transition counts");
    });

    private static Func<CancellationToken, Task<bool>> Record(RedisJobStore store) =>
        (Func<CancellationToken, Task<bool>>)typeof(RedisJobStore)
            .GetMethod("SampleLiveActivity", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<CancellationToken, Task<bool>>), store);

    [Test]
    public Task EmptySamplesExtendOnlyWhileTheLifecycleRevisionIsUnchanged() => WithStore(async (store, db, ns) =>
    {
        Func<CancellationToken, Task<bool>> record = Record(store);
        await record(default);
        await Task.Delay(1200);
        Check((await store.GetLiveActivity())[^1].RunningCount == 0, "Proven empty seconds were not reconstructed");
        string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        await db.StringIncrementAsync($"flywheel:{{{tag}}}:v1:revision");
        Check((await store.GetLiveActivity())[^1].RunningCount is null, "Changed revision fabricated an empty sample");
        await store.Enqueue(Request());
        await record(default);
        Check((await store.GetLiveActivity()).Any(p => p.QueuedCount == 1), "Idle sample suppressed a new job in the same second");
    });

    [Test]
    public Task IdleRecorderSleepsAndWakesOnNewJobs() => WithStore(async (store, db, ns) =>
    {
        string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        RedisKey samplesKey = $"flywheel:{{{tag}}}:v1:activity:samples";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var recorder = new RedisLiveActivityRecorder(store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisLiveActivityRecorder>.Instance);
        await recorder.StartAsync(default);
        try
        {
            RedisValue first;
            while ((first = await db.HashGetAsync(samplesKey, "latest")).IsNull)
                await Task.Delay(10, timeout.Token);
            await Task.Delay(1500, timeout.Token);
            Check(await db.HashGetAsync(samplesKey, "latest") == first, "Empty recorder continued its per-second polling");
            await store.Enqueue(Request());
            while (!(await store.GetLiveActivity()).Any(p => p.QueuedCount == 1))
                await Task.Delay(10, timeout.Token);
        }
        finally { await recorder.StopAsync(default); }
    });

    private static async Task<DateTime?> Expiry(IDatabase db, RedisKey key)
    {
        // Equivalent to PEXPIRETIME, including on the supported Redis 6 server used for local integration tests.
        long expiry = (long)await db.ScriptEvaluateAsync("""
            local ttl = redis.call('PTTL', KEYS[1])
            if ttl < 0 then return ttl end
            local clock = redis.call('TIME')
            return tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000) + ttl
            """, [key]);
        return expiry < 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(expiry).UtcDateTime;
    }

    [Test]
    public Task LiveConcurrencyIsRecordedWithoutDashboardConnections() => WithStore(async (store, db, ns) =>
    {
        await store.Enqueue(Request());
        await store.Enqueue(Request());
        await store.Enqueue(Request() with { Delay = TimeSpan.FromHours(1) });
        JobLease lease = (await store.Claim("recorder-test", TimeSpan.FromSeconds(30)))!;
        using var recorder = new RedisLiveActivityRecorder(store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisLiveActivityRecorder>.Instance);
        await recorder.StartAsync(default);
        try
        {
            await Task.Delay(3200);
            // A fresh reader has no browser state and never requested a dashboard subscription.
            var reopened = new RedisJobStore(_ => Task.FromResult(db), ns);
            IReadOnlyList<JobHistoryPoint> points = await reopened.GetLiveActivity();
            JobHistoryPoint[] recorded = points.Where(p => p.RunningCount.HasValue).ToArray();
            Check(recorded.Length >= 3, "The server did not record idle seconds without dashboard readers");
            Check(recorded.All(p => p.RunningCount == 1 && p.ScheduledCount == 1 && p.QueuedCount == 1),
                "Persisted concurrency must distinguish running, future scheduled, and eligible queued jobs");
            Check(points.Sum(p => p.Running) == 1, "Sampling changed lifecycle event counts");
        }
        finally { await recorder.StopAsync(default); }
    });

    [Test]
    public Task LiveActivityPreservesFastStartAndFinishTransitions() => WithStore(async store =>
    {
        await store.Enqueue(Request());
        JobLease lease = (await store.Claim("live-test", TimeSpan.FromSeconds(30)))!;
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Completion failed");
        Check(!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Stale completion accepted");

        IReadOnlyList<JobHistoryPoint> points = await store.GetLiveActivity();
        Check(points.Count == 61, "Live window must retain sixty seconds plus the current second");
        Check(points.Zip(points.Skip(1)).All(pair => pair.Second.Timestamp - pair.First.Timestamp == 1000), "Live buckets must be one second apart");
        Check(points.Sum(p => p.Scheduled) == 1 && points.Sum(p => p.Running) == 1 && points.Sum(p => p.Succeeded) == 1,
            "Fast jobs must retain both their start and completion, without counting rejected writes");
        IReadOnlyList<JobHistoryPoint> history = await store.GetHistory();
        Check(history.Sum(p => p.Running) == 1 && history.Sum(p => p.Succeeded) == 1, "Live activity changed historical counts");
    });
}
