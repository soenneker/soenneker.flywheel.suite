using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Responses;
using System.Threading;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task LiveActivityPrunesDocumentsOutsideItsVisibleWindow() => WithStore(async (store, db, ns) =>
    {
        long old = (await store.GetLiveActivity())[^1].Timestamp - 62000;
        await SeedRecord(db, ns, "live", old, new JobHistoryPoint(old, 99, 0, 0, 0));
        await SeedRecord(db, ns, "samples", old, new { scheduled = 99L, running = 99L, queued = 99L });
        Check((await store.GetLiveActivity()).All(p => p.Timestamp > old), "Expired samples remained visible");
        await store.Maintain(100);
        await using var database = OpenLibrarian(db, ns);
        Check(await (await database.GetContainer("flywheel.live")).GetItem(DocumentId(old)) is null, "Expired transition was retained");
        Check(await (await database.GetContainer("flywheel.samples")).GetItem(DocumentId(old)) is null, "Expired gauge was retained");
    });

    [Test]
    public Task LiveSamplesUseExactSecondsAndRejectMalformedDocuments() => WithStore(async (store, db, ns) =>
    {
        long stamp = (await store.GetLiveActivity())[^1].Timestamp - 10000;
        await SeedRecord(db, ns, "samples", stamp - 61000, new { scheduled = 99L, running = 99L, queued = 99L });
        await SeedRecord(db, ns, "live", stamp, new JobHistoryPoint(stamp, 2, 0, 0, 0));
        JobHistoryPoint point = (await store.GetLiveActivity()).Single(p => p.Timestamp == stamp);
        Check(point.Scheduled == 2 && point.RunningCount is null, "Old sample was reused for another second");
        await SeedRecord(db, ns, "samples", stamp, new { scheduled = 5L, running = 6L, queued = 7L });
        point = (await store.GetLiveActivity()).Single(p => p.Timestamp == stamp);
        Check(point.ScheduledCount == 5 && point.RunningCount == 6 && point.QueuedCount == 7, "Current sample was not read");
        await SeedRecord(db, ns, "samples", stamp, "invalid");
        try { await store.GetLiveActivity(); throw new Exception("Malformed sample fabricated a gauge count"); }
        catch (System.Text.Json.JsonException) { }
    });

    [Test]
    public Task LiveSamplesAreSharedAcrossStoresWithoutChangingDispatchRevision() => WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.Enqueue(Request());
        string? revision = await ControlValue(db, ns, "revision");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? store : other).SampleLiveActivity(default)));
        Check(await ControlValue(db, ns, "revision") == revision, "Sampler invalidated dispatch");
        var points = await other.GetLiveActivity();
        Check(points.Where(p => p.QueuedCount.HasValue).All(p => p.QueuedCount == 1 && p.RunningCount == 0 && p.ScheduledCount == 0),
            "Concurrent samplers produced inconsistent counts");
        await using var database = OpenLibrarian(db, ns);
        Check((await (await database.GetContainer("flywheel.samples")).GetAllIds()).Count <= 61, "Sample window exceeded its capacity");
        Check((await store.GetLiveActivity()).Sum(p => p.Scheduled) == 1, "Sampler changed transition counts");
    });

    private static Func<CancellationToken, Task<bool>> Record(RedisJobStore store) => store.SampleLiveActivity;

    [Test]
    public Task EmptySamplesExtendOnlyWhileTheLifecycleRevisionIsUnchanged() => WithStore(async (store, db, ns) =>
    {
        Func<CancellationToken, Task<bool>> record = Record(store);
        await record(default);
        await Task.Delay(1200);
        Check((await store.GetLiveActivity())[^1].RunningCount == 0, "Proven empty seconds were not reconstructed");
        await using var database = OpenLibrarian(db, ns);
        await (await database.GetContainer("flywheel.control")).UpdateItem("revision", Guid.NewGuid().ToString("N"));
        if (await ControlValue(db, ns, "revision") is null)
            await (await database.GetContainer("flywheel.control")).AddItem("revision", Guid.NewGuid().ToString("N"));
        Check((await store.GetLiveActivity())[^1].RunningCount is null, "Changed revision fabricated an empty sample");
        await store.Enqueue(Request());
        await record(default);
        Check((await store.GetLiveActivity()).Any(p => p.QueuedCount == 1), "Idle sample suppressed a new job in the same second");
    });

    [Test]
    public Task IdleRecorderSleepsAndWakesOnNewJobs() => WithStore(async (store, db, ns) =>
    {
        await using var database = OpenLibrarian(db, ns);
        var samples = await database.GetContainer("flywheel.samples");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var recorder = new RedisLiveActivityRecorder(store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisLiveActivityRecorder>.Instance);
        await recorder.StartAsync(default);
        try
        {
            while ((await samples.GetAllIds()).Count == 0) await Task.Delay(10, timeout.Token);
            string first = string.Join("|", await samples.GetAllItems());
            await Task.Delay(1500, timeout.Token);
            Check(string.Join("|", await samples.GetAllItems()) == first, "Empty recorder continued per-second sampling");
            await store.Enqueue(Request());
            while (!(await store.GetLiveActivity()).Any(p => p.QueuedCount == 1))
                await Task.Delay(10, timeout.Token);
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
