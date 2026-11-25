using Soenneker.Extensions.ValueTask;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    private LibrarianTable<long, JobHistoryPoint> _history => Session.History;
    private LibrarianTable<long, JobHistoryPoint> _live => Session.Live;
    private LibrarianTable<long, Sample> _samples => Session.Samples;
    private LibrarianTable<string, IdleSample> _idle => Session.Idle;


    private static JobHistoryPoint Increment(JobHistoryPoint point, JobState state) => state.Value switch
    {
        0 => point with { Scheduled = point.Scheduled + 1 },
        1 => point with { Running = point.Running + 1 },
        2 => point with { Succeeded = point.Succeeded + 1 },
        3 => point with { DeadLettered = point.DeadLettered + 1 },
        4 => point with { Cancelled = point.Cancelled + 1 },
        5 => point with { Waiting = point.Waiting + 1 },
        _ => point
    };

    private static int Shard(string id)
    {
        uint hash = 2166136261;
        foreach (char ch in id) hash = unchecked((hash ^ ch) * 16777619);
        return (int)(hash & 15);
    }

    private static Dictionary<long, JobHistoryPoint> CombineHistory(IReadOnlyList<LibrarianEntry<long, JobHistoryPoint>> entries)
    {
        var result = new Dictionary<long, JobHistoryPoint>();
        foreach (var entry in entries)
        {
            JobHistoryPoint point = entry.Value;
            if (result.TryGetValue(point.Timestamp, out var previous))
                point = point with { Scheduled = point.Scheduled + previous.Scheduled, Running = point.Running + previous.Running,
                    Succeeded = point.Succeeded + previous.Succeeded, Cancelled = point.Cancelled + previous.Cancelled,
                    Waiting = point.Waiting + previous.Waiting, DeadLettered = point.DeadLettered + previous.DeadLettered };
            result[point.Timestamp] = point;
        }
        return result;
    }

    private async Task RecordTransition(JobState state, long now, string id)
    {
        int shard = Shard(id);
        long bucket = now / 300000 * 300000;
        await _history.Set(bucket + shard, Increment((await _history.Get(bucket + shard)) ?? new JobHistoryPoint(bucket, 0, 0, 0, 0), state)).NoSync();
        long second = now / 1000 * 1000;
        await _live.Set(second + shard, Increment((await _live.Get(second + shard)) ?? new JobHistoryPoint(second, 0, 0, 0, 0), state)).NoSync();
    }

    private async Task PruneLive(long now, int take = 128)
    {
        long first = now / 1000 * 1000 - 60000;
        foreach (var entry in await _live.Range("key", maximum: first - 1, take: take)) await _live.Remove(entry.Key).NoSync();
        foreach (var entry in await _samples.Range("key", maximum: first - 1, take: take)) await _samples.Remove(entry.Key).NoSync();
    }

    public Task<bool> SampleLiveActivity(CancellationToken cancellationToken)
    {
        return Mutate(cancellationToken, async now =>
        {
            await PruneLive(now);
            long running = await _jobs.Count("state", JobState.Running.Value);
            long pending = await _jobs.Count("state", JobState.Scheduled.Value);
            long queued = await _jobs.CountRange("scheduledOrder", minimum: "", maximum: ScheduledUpperBound(now));
            long scheduled = pending - queued;
            await _samples.Set(now / 1000 * 1000, new Sample(scheduled, running, queued)).NoSync();
            bool active = scheduled + running + queued != 0;
            if (!active) await _idle.Set("latest", new IdleSample(now / 1000 * 1000, Session.LifecycleRevision)).NoSync();
            else await _idle.Remove("latest").NoSync();
            return active;
        });
    }

    public Task<IReadOnlyList<JobHistoryPoint>> GetLiveActivity(CancellationToken cancellationToken = default) =>
        ReadSnapshot<IReadOnlyList<JobHistoryPoint>>(cancellationToken, async now =>
        {
            var result = new List<JobHistoryPoint>(61);
            long last = now / 1000 * 1000;
            var points = CombineHistory(await _live.Range("key", last - 60000, last + 15));
            var samples = (await _samples.Range("key", last - 60000, last)).ToDictionary(p => p.Key, p => p.Value);
            IdleSample? idle = await _idle.Get("latest");
            for (long stamp = last - 60000; stamp <= last; stamp += 1000)
            {
                JobHistoryPoint point = points.GetValueOrDefault(stamp) ?? new JobHistoryPoint(stamp, 0, 0, 0, 0);
                if (samples.TryGetValue(stamp, out Sample sample))
                    point = point with { ScheduledCount = sample.Scheduled, RunningCount = sample.Running, QueuedCount = sample.Queued };
                else if (idle is not null && idle.Revision == Session.LifecycleRevision && stamp >= idle.Timestamp)
                    point = point with { ScheduledCount = 0, RunningCount = 0, QueuedCount = 0 };
                result.Add(point);
            }
            return result;
        });

    public Task<IReadOnlyList<JobHistoryPoint>> GetHistory(DateTimeOffset startAt, DateTimeOffset endAt,
        CancellationToken cancellationToken = default) =>
        ReadSnapshot<IReadOnlyList<JobHistoryPoint>>(cancellationToken, async now =>
        {
            long start = startAt.ToUnixTimeMilliseconds(), end = endAt.ToUnixTimeMilliseconds();
            if (start >= end || end > now + 300000 || start < end - HistoryRetention.TotalMilliseconds)
                throw new ArgumentOutOfRangeException(nameof(startAt));
            var points = CombineHistory(await _history.Range("key", start / 300000 * 300000, (end - 1) / 300000 * 300000 + 15));
            var result = new List<JobHistoryPoint>();
            for (long stamp = start / 300000 * 300000; stamp <= (end - 1) / 300000 * 300000; stamp += 300000)
                result.Add(points.GetValueOrDefault(stamp) ?? new JobHistoryPoint(stamp, 0, 0, 0, 0));
            return result;
        });

    public async Task<IReadOnlyList<JobHistoryPoint>> GetHistory(CancellationToken cancellationToken = default)
    {
        DateTimeOffset end = DateTimeOffset.FromUnixTimeMilliseconds(await _clock(cancellationToken).NoSync());
        return await GetHistory(end - (HistoryRetention < TimeSpan.FromDays(1) ? HistoryRetention : TimeSpan.FromDays(1)), end, cancellationToken);
    }
}
