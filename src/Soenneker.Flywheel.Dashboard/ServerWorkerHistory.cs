using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Quark;

namespace Soenneker.Flywheel.Dashboard;

internal sealed class ServerWorkerHistory
{
    public RealtimeChartData Data { get; } = new(61, "Busy workers");
    public ChartOptions Options { get; private set; } = CreateOptions(1);

    public void Update(ServerView server)
    {
        long last = server.ObservedAt / 5000 * 5000;
        if (last <= 0 || (Data.Count > 0 && last < Data.XValues[^1])) return;
        IReadOnlyList<ServerWorkerHistoryPoint> history = server.WorkerHistory;
        int peak = history.Count == 0 ? 0 : history.Max(point => point.BusyWorkers);
        if (peak > Options.Maximum)
            Options = CreateOptions(Math.Pow(2, Math.Ceiling(Math.Log2(peak * 1.25))));

        Data.Clear();
        int index = -1;
        for (long timestamp = last - 300000; timestamp <= last; timestamp += 5000)
        {
            while (index + 1 < history.Count && history[index + 1].Timestamp <= timestamp) index++;
            ServerWorkerHistoryPoint? observation = index >= 0 ? history[index] : null;
            Data.Append(DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
                observation is not null && timestamp < observation.ExpiresAt ? observation.BusyWorkers : (double?)null);
        }
    }
    private static ChartOptions CreateOptions(double maximum) => new()
    {
        Width = 1200, Height = 110, Minimum = 0, Maximum = maximum, Animate = false,
        ShowYAxis = false, ShowGrid = false, PaddingLeft = 24, ClipPlot = true,
        ShowPoints = false, Curve = ChartCurve.Monotone,
        EnableRealtimeScrolling = true, RealtimeScrollDuration = TimeSpan.FromSeconds(5),
        MaximumXAxisLabels = 5, Palette = ["#0ea5e9"],
        LabelFormatter = label => label.Length > 8 ? label[..8] : label
    };
}
