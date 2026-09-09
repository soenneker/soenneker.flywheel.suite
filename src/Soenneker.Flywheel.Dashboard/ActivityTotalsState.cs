using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard;

internal sealed class ActivityTotalsState
{
    public double[]? Totals { get; private set; }
    public long? RecurringCount { get; private set; }
    public long? QueuedCount { get; private set; }
    public long? ScheduledCount { get; private set; }
    public long? RunningCount { get; private set; }
    public int? ServerCount { get; private set; }
    public int? TotalWorkers { get; private set; }
    public bool Live { get; private set; }
    public void UpdateScheduled(long? count) { ScheduledCount = count; Changed?.Invoke(); }
    public void UpdateRunning(long? count) { RunningCount = count; Changed?.Invoke(); }
    public void UpdateServers(int? serverCount, int? totalWorkers) { ServerCount = serverCount; TotalWorkers = totalWorkers; Changed?.Invoke(); }
    public void UpdateLive(bool live) { if (Live == live) return; Live = live; Changed?.Invoke(); }

    public void UpdateSnapshot(LiveBoard snapshot)
    {
        long? scheduled = snapshot.Schedules?.Scheduled.Count(job => job.State == "Scheduled");
        long? queued = snapshot.Schedules?.Scheduled.Count(job => job.State == "Queued");
        double[]? values = Totals;
        if (LastHour && snapshot.History is { } history)
        {
            long first = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds() / 300000 * 300000;
            values = [0, 0, 0];
            foreach (JobHistoryPoint point in history)
            {
                if (point.Timestamp < first) continue;
                values[0] += point.Scheduled;
                values[1] += point.Succeeded;
                values[2] += point.DeadLettered;
            }
        }
        bool changed = RecurringCount != snapshot.RecurringCount || QueuedCount != queued || ScheduledCount != scheduled || RunningCount != snapshot.RunningCount ||
            ServerCount != snapshot.ServerCount || TotalWorkers != snapshot.TotalWorkers ||
            (Totals is null ? values is not null : values is null || !Totals.AsSpan().SequenceEqual(values));
        RecurringCount = snapshot.RecurringCount;
        QueuedCount = queued;
        ScheduledCount = scheduled;
        RunningCount = snapshot.RunningCount;
        ServerCount = snapshot.ServerCount;
        TotalWorkers = snapshot.TotalWorkers;
        Totals = values;
        if (changed) Changed?.Invoke();
    }
    public void Clear()
    {
        Totals = null;
        RecurringCount = null;
        QueuedCount = null;
        ScheduledCount = null;
        RunningCount = null;
        ServerCount = null;
        TotalWorkers = null;
        Live = false;
        Changed?.Invoke();
    }
    public bool LastHour { get; set; } = true;
    public event Action? Changed;

    public void Update(double[] totals)
    {
        if (Totals is not null && Totals.AsSpan().SequenceEqual(totals)) return;
        Totals = totals;
        Changed?.Invoke();
    }
}
