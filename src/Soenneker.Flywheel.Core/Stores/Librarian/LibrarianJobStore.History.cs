using Soenneker.Extensions.ValueTask;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    private readonly LibrarianTable<long, JobHistoryPoint> _history;
    private readonly LibrarianTable<long, JobHistoryPoint> _live;
    private readonly LibrarianTable<long, Sample> _samples;
    private readonly LibrarianTable<string, IdleSample> _idle;

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

    private async Task RecordTransition(JobState state, long now)
    {
        long bucket = now / 300000 * 300000;
        await _history.Set(bucket, Increment((await _history.Get(bucket)) ?? new JobHistoryPoint(bucket, 0, 0, 0, 0), state)).NoSync();
        long second = now / 1000 * 1000;
        await _live.Set(second, Increment((await _live.Get(second)) ?? new JobHistoryPoint(second, 0, 0, 0, 0), state)).NoSync();
        await PruneLive(now);
    }

    private async Task PruneLive(long now)
    {
        long first = now / 1000 * 1000 - 60000;
        foreach (long stamp in (await _live.GetKeys()).Where(t => t < first).ToArray()) await _live.Remove(stamp).NoSync();
        foreach (long stamp in (await _samples.GetKeys()).Where(t => t < first).ToArray()) await _samples.Remove(stamp).NoSync();
    }

    public Task<bool> SampleLiveActivity(CancellationToken cancellationToken)
    {
        return Mutate(cancellationToken, async now =>
        {
            long running = await _jobs.Count("state", JobState.Running.Value);
            var pending = await _jobs.Find("state", JobState.Scheduled.Value);
            long queued = pending.LongCount(j => j.DueAt <= now);
            long scheduled = pending.Count - queued;
            await _samples.Set(now / 1000 * 1000, new Sample(scheduled, running, queued)).NoSync();
            bool active = scheduled + running + queued != 0;
            if (!active) await _idle.Set("latest", new IdleSample(now / 1000 * 1000, _lifecycleRevision)).NoSync();
            else await _idle.Remove("latest").NoSync();
            await PruneLive(now);
            return active;
        });
    }

    public Task<IReadOnlyList<JobHistoryPoint>> GetLiveActivity(CancellationToken cancellationToken = default) =>
        Mutate<IReadOnlyList<JobHistoryPoint>>(cancellationToken, async now =>
        {
            var result = new List<JobHistoryPoint>(61);
            long last = now / 1000 * 1000;
            var points = (await _live.Range("key", last - 60000, last)).ToDictionary(p => p.Key, p => p.Value);
            var samples = (await _samples.Range("key", last - 60000, last)).ToDictionary(p => p.Key, p => p.Value);
            IdleSample? idle = await _idle.Get("latest");
            for (long stamp = last - 60000; stamp <= last; stamp += 1000)
            {
                JobHistoryPoint point = points.GetValueOrDefault(stamp) ?? new JobHistoryPoint(stamp, 0, 0, 0, 0);
                if (samples.TryGetValue(stamp, out Sample sample))
                    point = point with { ScheduledCount = sample.Scheduled, RunningCount = sample.Running, QueuedCount = sample.Queued };
                else if (idle is not null && idle.Revision == _lifecycleRevision && stamp >= idle.Timestamp)
                    point = point with { ScheduledCount = 0, RunningCount = 0, QueuedCount = 0 };
                result.Add(point);
            }
            return result;
        });

    public Task<IReadOnlyList<JobHistoryPoint>> GetHistory(DateTimeOffset startAt, DateTimeOffset endAt,
        CancellationToken cancellationToken = default) =>
        Mutate<IReadOnlyList<JobHistoryPoint>>(cancellationToken, async now =>
        {
            long start = startAt.ToUnixTimeMilliseconds(), end = endAt.ToUnixTimeMilliseconds();
            if (start >= end || end > now + 300000 || start < end - HistoryRetention.TotalMilliseconds)
                throw new ArgumentOutOfRangeException(nameof(startAt));
            var points = (await _history.Range("key", start / 300000 * 300000, (end - 1) / 300000 * 300000)).ToDictionary(p => p.Key, p => p.Value);
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
