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
    public ValueTask LiveActivityPrunesDocumentsOutsideItsVisibleWindow(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        long old = (await store.GetLiveActivity(cancellationToken: cancellationToken))[^1].Timestamp - 62000;
        await SeedRecord(db, ns, "live", old, new JobHistoryPoint(old, 99, 0, 0, 0));
        await SeedRecord(db, ns, "samples", old, new { scheduled = 99L, running = 99L, queued = 99L });
        Check((await store.GetLiveActivity(cancellationToken: cancellationToken)).All(p => p.Timestamp > old), "Expired samples remained visible");
        await store.Maintain(100, cancellationToken: cancellationToken);
        await using var database = OpenLibrarian(db, ns);
        Check(await (await database.GetContainer("flywheel.live", cancellationToken: cancellationToken)).GetItem(DocumentId(old), cancellationToken: cancellationToken) is null, "Expired transition was retained");
        Check(await (await database.GetContainer("flywheel.samples", cancellationToken: cancellationToken)).GetItem(DocumentId(old), cancellationToken: cancellationToken) is null, "Expired gauge was retained");
    }));

    [Test]
    public ValueTask LiveSamplesUseExactSecondsAndRejectMalformedDocuments(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        long stamp = (await store.GetLiveActivity(cancellationToken: cancellationToken))[^1].Timestamp - 10000;
        await SeedRecord(db, ns, "samples", stamp - 61000, new { scheduled = 99L, running = 99L, queued = 99L });
        await SeedRecord(db, ns, "live", stamp, new JobHistoryPoint(stamp, 2, 0, 0, 0));
        JobHistoryPoint point = (await store.GetLiveActivity(cancellationToken: cancellationToken)).Single(p => p.Timestamp == stamp);
        Check(point.Scheduled == 2 && point.RunningCount is null, "Old sample was reused for another second");
        await SeedRecord(db, ns, "samples", stamp, new { scheduled = 5L, running = 6L, queued = 7L });
        point = (await store.GetLiveActivity(cancellationToken: cancellationToken)).Single(p => p.Timestamp == stamp);
        Check(point.ScheduledCount == 5 && point.RunningCount == 6 && point.QueuedCount == 7, "Current sample was not read");
        await SeedRecord(db, ns, "samples", stamp, "invalid");
        try { await store.GetLiveActivity(cancellationToken: cancellationToken); throw new Exception("Malformed sample fabricated a gauge count"); }
        catch (System.Text.Json.JsonException) { }
    }));

    [Test]
    public ValueTask LiveSamplesAreSharedAcrossStoresWithoutChangingDispatchRevision(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns);
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        string? revision = await ControlValue(db, ns, "revision");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? store : other).SampleLiveActivity(cancellationToken)));
        Check(await ControlValue(db, ns, "revision") == revision, "Sampler invalidated dispatch");
        var points = await other.GetLiveActivity(cancellationToken: cancellationToken);
        Check(points.Where(p => p.QueuedCount.HasValue).All(p => p.QueuedCount == 1 && p.RunningCount == 0 && p.ScheduledCount == 0),
            "Concurrent samplers produced inconsistent counts");
        await using var database = OpenLibrarian(db, ns);
        Check((await (await database.GetContainer("flywheel.samples", cancellationToken: cancellationToken)).GetAllIds(cancellationToken: cancellationToken)).Count <= 61, "Sample window exceeded its capacity");
        Check((await store.GetLiveActivity(cancellationToken: cancellationToken)).Sum(p => p.Scheduled) == 1, "Sampler changed transition counts");
    }));

    private static Func<CancellationToken, Task<bool>> Record(RedisJobStore store) => store.SampleLiveActivity;

    [Test]
    public ValueTask EmptySamplesExtendOnlyWhileTheLifecycleRevisionIsUnchanged(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        Func<CancellationToken, Task<bool>> record = Record(store);
        await record(cancellationToken);
        await Task.Delay(1200, cancellationToken: cancellationToken);
        Check((await store.GetLiveActivity(cancellationToken: cancellationToken))[^1].RunningCount == 0, "Proven empty seconds were not reconstructed");
        await using var database = OpenLibrarian(db, ns);
        await (await database.GetContainer("flywheel.control", cancellationToken: cancellationToken)).UpdateItem("revision", Guid.NewGuid().ToString("N"), cancellationToken: cancellationToken);
        if (await ControlValue(db, ns, "revision") is null)
            await (await database.GetContainer("flywheel.control", cancellationToken: cancellationToken)).AddItem("revision", Guid.NewGuid().ToString("N"), cancellationToken: cancellationToken);
        Check((await store.GetLiveActivity(cancellationToken: cancellationToken))[^1].RunningCount is null, "Changed revision fabricated an empty sample");
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        await record(cancellationToken);
        Check((await store.GetLiveActivity(cancellationToken: cancellationToken)).Any(p => p.QueuedCount == 1), "Idle sample suppressed a new job in the same second");
    }));

    [Test]
    public ValueTask IdleRecorderSleepsAndWakesOnNewJobs(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        await using var database = OpenLibrarian(db, ns);
        var samples = await database.GetContainer("flywheel.samples", cancellationToken: cancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var recorder = new RedisLiveActivityRecorder(store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisLiveActivityRecorder>.Instance);
        await recorder.StartAsync(cancellationToken);
        try
        {
            while ((await samples.GetAllIds(cancellationToken: cancellationToken)).Count == 0) await Task.Delay(10, timeout.Token);
            string first = string.Join("|", await samples.GetAllItems(cancellationToken: cancellationToken));
            await Task.Delay(1500, timeout.Token);
            Check(string.Join("|", await samples.GetAllItems(cancellationToken: cancellationToken)) == first, "Empty recorder continued per-second sampling");
            await store.Enqueue(Request(), cancellationToken: cancellationToken);
            while (!(await store.GetLiveActivity(cancellationToken: cancellationToken)).Any(p => p.QueuedCount == 1))
                await Task.Delay(10, timeout.Token);
        }
        finally { await recorder.StopAsync(default); }
    }));

    [Test]
    public ValueTask LiveConcurrencyIsRecordedWithoutDashboardConnections(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        await store.Enqueue(Request() with { Delay = TimeSpan.FromHours(1) }, cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("recorder-test", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        using var recorder = new RedisLiveActivityRecorder(store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisLiveActivityRecorder>.Instance);
        await recorder.StartAsync(cancellationToken);
        try
        {
            await Task.Delay(3200, cancellationToken: cancellationToken);
            // A fresh reader has no browser state and never requested a dashboard subscription.
            var reopened = new RedisJobStore(_ => Task.FromResult(db), ns);
            IReadOnlyList<JobHistoryPoint> points = await reopened.GetLiveActivity(cancellationToken: cancellationToken);
            JobHistoryPoint[] recorded = points.Where(p => p.RunningCount.HasValue).ToArray();
            Check(recorded.Length >= 3, "The server did not record idle seconds without dashboard readers");
            Check(recorded.All(p => p.RunningCount == 1 && p.ScheduledCount == 1 && p.QueuedCount == 1),
                "Persisted concurrency must distinguish running, future scheduled, and eligible queued jobs");
            Check(points.Sum(p => p.Running) == 1, "Sampling changed lifecycle event counts");
        }
        finally { await recorder.StopAsync(default); }
    }));

    [Test]
    public ValueTask LiveActivityPreservesFastStartAndFinishTransitions(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("live-test", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Completion failed");
        Check(!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Stale completion accepted");

        IReadOnlyList<JobHistoryPoint> points = await store.GetLiveActivity(cancellationToken: cancellationToken);
        Check(points.Count == 61, "Live window must retain sixty seconds plus the current second");
        Check(points.Zip(points.Skip(1)).All(pair => pair.Second.Timestamp - pair.First.Timestamp == 1000), "Live buckets must be one second apart");
        Check(points.Sum(p => p.Scheduled) == 1 && points.Sum(p => p.Running) == 1 && points.Sum(p => p.Succeeded) == 1,
            "Fast jobs must retain both their start and completion, without counting rejected writes");
        IReadOnlyList<JobHistoryPoint> history = await store.GetHistory(cancellationToken: cancellationToken);
        Check(history.Sum(p => p.Running) == 1 && history.Sum(p => p.Succeeded) == 1, "Live activity changed historical counts");
    }));
}
