namespace Soenneker.Flywheel.Dashboard;

internal sealed class ActivityTotalsState
{
    public double[]? Totals { get; private set; }
    public long? ScheduledCount { get; private set; }
    public long? RunningCount { get; private set; }
    public int? ServerCount { get; private set; }
    public int? TotalWorkers { get; private set; }
    public bool Live { get; private set; }
    public void UpdateScheduled(long? count) { ScheduledCount = count; Changed?.Invoke(); }
    public void UpdateRunning(long? count) { RunningCount = count; Changed?.Invoke(); }
    public void UpdateServers(int? serverCount, int? totalWorkers) { ServerCount = serverCount; TotalWorkers = totalWorkers; Changed?.Invoke(); }
    public void UpdateLive(bool live) { Live = live; Changed?.Invoke(); }
    public void Clear()
    {
        Totals = null;
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
        Totals = totals;
        Changed?.Invoke();
    }
}
