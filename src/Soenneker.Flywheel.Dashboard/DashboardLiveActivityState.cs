using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Quark;

namespace Soenneker.Flywheel.Dashboard;

internal sealed class DashboardLiveActivityState(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly RealtimeChartData _liveActivity = new(61, "Scheduled", "Running", "Succeeded", "Failed", "Queued");
    private List<JobHistoryPoint>? _latestLiveActivity;
    private readonly Dictionary<long, JobHistoryPoint> _activityBuckets = [];
    private bool _historyChanged;
    private long _liveReceivedAt;
    private long _liveClockAnchor;
    private long _liveWindowEnd;
    private readonly Dictionary<long, double?> _runningSamples = [];
    private readonly Dictionary<long, double?> _scheduledSamples = [];
    private readonly Dictionary<long, double?> _queuedSamples = [];
    public RealtimeChartData Data => _liveActivity;

    public void Update(List<JobHistoryPoint> activity, ActivityTotalsState totals)
    {
        bool firstActivity = _latestLiveActivity is not { Count: > 0 };
        _latestLiveActivity = activity;
        _activityBuckets.Clear();
        _historyChanged = true;
        if (firstActivity && activity.Count > 0)
        {
            _liveReceivedAt = _timeProvider.GetTimestamp();
            _liveClockAnchor = activity[^1].Timestamp;
        }
        foreach (JobHistoryPoint point in activity)
        {
            _activityBuckets[point.Timestamp] = point;
            if (point.RunningCount is { } running) _runningSamples[point.Timestamp] = running;
            if (point.ScheduledCount is { } scheduled) _scheduledSamples[point.Timestamp] = scheduled;
            if (point.QueuedCount is { } queued) _queuedSamples[point.Timestamp] = queued;
        }
        Sample(totals);
    }

    public void Sample(ActivityTotalsState? totals)
    {
        if (_latestLiveActivity is not { Count: > 0 }) return;
        long last = Math.Max(_liveWindowEnd, _liveClockAnchor + (long)_timeProvider.GetElapsedTime(_liveReceivedAt).TotalSeconds * 1000);
        // Timers can run late. Carry the last observed gauges through elapsed seconds
        // before recording the newest observation, without inventing pre-session data.
        if (_runningSamples.Count > 0)
        {
            long previous = _runningSamples.Keys.Max();
            for (long timestamp = Math.Max(previous + 1000, last - 60000); timestamp < last; timestamp += 1000)
            {
                _runningSamples[timestamp] = _runningSamples[previous];
                _scheduledSamples[timestamp] = _scheduledSamples.GetValueOrDefault(previous);
                _queuedSamples[timestamp] = _queuedSamples.GetValueOrDefault(previous);
            }
        }
        _liveWindowEnd = last;
        _runningSamples[last] = totals?.Live == false ? null : totals?.RunningCount;
        _scheduledSamples[last] = totals?.Live == false ? null : totals?.ScheduledCount;
        _queuedSamples[last] = totals?.Live == false ? null : totals?.QueuedCount;
        foreach (long expired in _runningSamples.Keys.Where(timestamp => timestamp < last - 60000).ToArray())
        {
            _runningSamples.Remove(expired);
            _scheduledSamples.Remove(expired);
            _queuedSamples.Remove(expired);
        }
    }

    public void Advance(ActivityTotalsState? totals)
    {
        if (_latestLiveActivity is not { Count: > 0 } points) return;
        Sample(totals);
        long last = _liveWindowEnd;
        long first = last - 60000;
        Span<double?> values = stackalloc double?[5];
        if (_liveActivity.Count > 0 && _liveActivity.XValues[^1] < first)
            _liveActivity.Clear();

        // Reconcile server corrections only when a new history snapshot arrives.
        // Otherwise only the current bucket can change before the next append.
        int start = _historyChanged ? 0 : Math.Max(0, _liveActivity.Count - 1);
        for (int index = start; index < _liveActivity.Count; index++)
        {
            long timestamp = (long)_liveActivity.XValues[index];
            if (timestamp < first) continue;
            ReadValues(timestamp, points[^1].Timestamp, values);
            if (Matches(index, values)) continue;
            // The published buffer API is append-only. Rebuild only for an actual
            // correction; ordinary clock ticks retain samples and their label cache.
            _liveActivity.Clear();
            break;
        }
        long next = _liveActivity.Count == 0 ? first : (long)_liveActivity.XValues[^1] + 1000;
        for (long timestamp = next; timestamp <= last; timestamp += 1000)
        {
            ReadValues(timestamp, points[^1].Timestamp, values);
            _liveActivity.Append(DateTimeOffset.FromUnixTimeMilliseconds(timestamp), values);
        }
        _historyChanged = false;
    }

    private bool Matches(int index, ReadOnlySpan<double?> values)
    {
        for (int series = 0; series < values.Length; series++)
            if (_liveActivity.Series[series].Values[index] != values[series]) return false;
        return true;
    }

    private void ReadValues(long timestamp, long latestHistory, Span<double?> values)
    {
        _activityBuckets.TryGetValue(timestamp, out JobHistoryPoint? point);
        values[0] = (double?)point?.ScheduledCount ?? _scheduledSamples.GetValueOrDefault(timestamp);
        values[1] = (double?)point?.RunningCount ?? _runningSamples.GetValueOrDefault(timestamp);
        values[2] = point is not null ? point.Succeeded : timestamp > latestHistory ? 0 : null;
        values[3] = point is not null ? point.DeadLettered : timestamp > latestHistory ? 0 : null;
        values[4] = (double?)point?.QueuedCount ?? _queuedSamples.GetValueOrDefault(timestamp);
    }
}
