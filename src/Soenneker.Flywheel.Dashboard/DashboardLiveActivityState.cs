using System.Diagnostics;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Quark;

namespace Soenneker.Flywheel.Dashboard;

internal sealed class DashboardLiveActivityState
{
    private readonly RealtimeChartData _liveActivity = new(61, "Scheduled", "Running", "Succeeded", "Failed", "Queued");
    private List<JobHistoryPoint>? _latestLiveActivity;
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
        if (firstActivity && activity.Count > 0)
        {
            _liveReceivedAt = Stopwatch.GetTimestamp();
            _liveClockAnchor = activity[^1].Timestamp;
        }
        foreach (JobHistoryPoint point in activity)
        {
            if (point.RunningCount is { } running) _runningSamples[point.Timestamp] = running;
            if (point.ScheduledCount is { } scheduled) _scheduledSamples[point.Timestamp] = scheduled;
            if (point.QueuedCount is { } queued) _queuedSamples[point.Timestamp] = queued;
        }
        Sample(totals);
    }

    public void Sample(ActivityTotalsState? totals)
    {
        if (_latestLiveActivity is not { Count: > 0 }) return;
        long last = Math.Max(_liveWindowEnd, _liveClockAnchor + (long)Stopwatch.GetElapsedTime(_liveReceivedAt).TotalSeconds * 1000);
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

    public void Advance(ActivityTotalsState? ActivityTotals)
    {
        if (_latestLiveActivity is not { Count: > 0 } points) return;
        Sample(ActivityTotals);
        long last = _liveWindowEnd;
        Dictionary<long, JobHistoryPoint> buckets = points.ToDictionary(point => point.Timestamp);
        _liveActivity.Clear();
        for (long timestamp = last - 60000; timestamp <= last; timestamp += 1000)
        {
            if (buckets.TryGetValue(timestamp, out JobHistoryPoint? point))
                _liveActivity.Append(DateTimeOffset.FromUnixTimeMilliseconds(timestamp), (double?)point.ScheduledCount ?? _scheduledSamples.GetValueOrDefault(timestamp), (double?)point.RunningCount ?? _runningSamples.GetValueOrDefault(timestamp), point.Succeeded, point.DeadLettered, (double?)point.QueuedCount ?? _queuedSamples.GetValueOrDefault(timestamp));
            else if (timestamp > points[^1].Timestamp)
                _liveActivity.Append(DateTimeOffset.FromUnixTimeMilliseconds(timestamp), _scheduledSamples.GetValueOrDefault(timestamp), _runningSamples.GetValueOrDefault(timestamp), 0, 0, _queuedSamples.GetValueOrDefault(timestamp));
            else
                _liveActivity.Append(DateTimeOffset.FromUnixTimeMilliseconds(timestamp), _scheduledSamples.GetValueOrDefault(timestamp), _runningSamples.GetValueOrDefault(timestamp), null, null, _queuedSamples.GetValueOrDefault(timestamp));
        }
    }

}
