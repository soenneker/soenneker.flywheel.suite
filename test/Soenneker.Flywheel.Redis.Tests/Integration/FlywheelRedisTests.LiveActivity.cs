using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Responses;

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
            foreach (var point in points.Where(p => p.RunningCount.HasValue || p.Scheduled > 0))
            {
                DateTime? expiry = await db.KeyExpireTimeAsync($"flywheel:{{{tag}}}:v1:activity:{point.Timestamp / 1000}");
                Check(expiry == DateTimeOffset.FromUnixTimeMilliseconds(point.Timestamp + 61000).UtcDateTime,
                    "Live samples outlive their last visible second or their TTL was extended by another writer");
            }
        }
        finally { await recorder.StopAsync(default); }
    });

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
