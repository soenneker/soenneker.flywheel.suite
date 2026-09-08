using System.Reflection;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Quark;
using DashboardPage = Soenneker.Flywheel.Dashboard.Pages.Dashboard.Dashboard;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardSearchChartTests
{
    [Test]
    public void LegendExclusionsMapFailedAndScheduledGroupsToTableStatuses()
    {
        var page = new DashboardPage();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var hidden = (HashSet<string>)typeof(DashboardPage).GetField("_hiddenActivitySeries", flags)!.GetValue(page)!;
        hidden.Add("DeadLettered");
        hidden.Add("Scheduled");
        var isHidden = typeof(DashboardPage).GetMethod("IsActivitySeriesHidden", flags)!;
        if (!(bool)isHidden.Invoke(page, ["Failed"])! || !(bool)isHidden.Invoke(page, ["Queued / scheduled"])! ||
            (bool)isHidden.Invoke(page, ["Running"])!)
            throw new Exception("Legend names and table status filters disagree");
    }

    [Test]
    public void FreshChartLoadsTheFullServerRecordedWindow()
    {
        var state = new DashboardLiveActivityState();
        var totals = new ActivityTotalsState();
        totals.UpdateLive(true);
        totals.UpdateRunning(99);
        var points = Enumerable.Range(0, 61).Select(i => new JobHistoryPoint(i * 1000, 0, 0, i % 2, 0)
        {
            ScheduledCount = 8, RunningCount = i % 3, QueuedCount = 5
        }).ToList();
        state.Update(points, totals);
        state.Advance(totals);
        if (state.Data.XValues.Count != 61 || state.Data.Series[1].Values.Where((value, i) => value != i % 3).Any() ||
            state.Data.Series[0].Values.Any(value => value != 8) || state.Data.Series[4].Values.Any(value => value != 5))
            throw new Exception("A new browser chart lost recorded history or replaced it with current totals");
    }

    [Test]
    public void PendingJobsBecomeQueuedExactlyWhenDue()
    {
        var job = new Soenneker.Flywheel.Communication.Dtos.JobRecord
        {
            Id = "job", Name = "job", Payload = "{}", Policy = new(),
            State = Soenneker.Flywheel.Communication.Enums.JobState.Scheduled, DueAt = 1000
        };
        if (job.DisplayState(999) != "Scheduled" || job.DisplayState(1000) != "Queued" || job.DisplayState(2000) != "Queued")
            throw new Exception("Pending status must reflect whether work is eligible");
        if ((job with { State = Soenneker.Flywheel.Communication.Enums.JobState.Running }).DisplayState(2000) != "Running")
            throw new Exception("Eligibility must not override executing jobs");
    }

    [Test]
    public void RunningSeriesRetainsConcurrencySamplesInsteadOfStartEvents()
    {
        var page = new DashboardLiveActivityState();
        var totals = new ActivityTotalsState();
        totals.UpdateLive(true);
        totals.UpdateRunning(3);
        var type = typeof(DashboardLiveActivityState);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        type.GetField("_latestLiveActivity", flags)!.SetValue(page, new List<JobHistoryPoint> { new(60000, 0, 1, 1, 0) });
        type.GetField("_liveClockAnchor", flags)!.SetValue(page, 60000L);
        type.GetField("_liveReceivedAt", flags)!.SetValue(page, System.Diagnostics.Stopwatch.GetTimestamp());
        var apply = type.GetMethod("Advance")!;
        apply.Invoke(page, [totals]);
        var data = (RealtimeChartData)type.GetField("_liveActivity", flags)!.GetValue(page)!;
        if (data.Series[1].Name != "Running" || data.Series[1].Values[^1] != 3 || data.Series.Count != 5 || data.Series.Any(series => series.Name == "Started"))
            throw new Exception("Running concurrency was confused with start events");
        totals.UpdateRunning(0);
        type.GetField("_liveClockAnchor", flags)!.SetValue(page, 61000L);
        apply.Invoke(page, [totals]);
        if (data.Series[1].Values[^1] != 0 || data.Series[1].Values[^2] != 3)
            throw new Exception("Running samples did not retain the change in concurrency");
    }

    [Test]
    public void LiveChartKeepsColorsAndAdvancesIdleTimeWithoutGapsOrRewinding()
    {
        var type = typeof(DashboardLiveActivityState);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var options = (ChartOptions)typeof(DashboardPage).GetField("LiveActivityOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        string[] colors = ["#8b5cf6", "#0ea5e9", "#10b981", "#f97316", "#6366f1"];
        if (!options.Palette.SequenceEqual(colors)) throw new Exception("Live series lost their status colors");
        if (!options.EnableRealtimeScrolling || options.RealtimeScrollDuration != TimeSpan.FromSeconds(1) || options.Curve != ChartCurve.Monotone) throw new Exception("Live scrolling must interpolate the full one-second sampling interval with curves that do not overshoot");
        var page = new DashboardLiveActivityState();
        var totals = new ActivityTotalsState();
        totals.UpdateLive(true);
        type.GetField("_latestLiveActivity", flags)!.SetValue(page, new List<JobHistoryPoint> { new(60000, 0, 1, 1, 0) });
        type.GetField("_liveClockAnchor", flags)!.SetValue(page, 60000L);
        type.GetField("_liveReceivedAt", flags)!.SetValue(page, System.Diagnostics.Stopwatch.GetTimestamp() - 2 * System.Diagnostics.Stopwatch.Frequency);
        var apply = type.GetMethod("Advance")!;
        apply.Invoke(page, [totals]);
        var data = (RealtimeChartData)type.GetField("_liveActivity", flags)!.GetValue(page)!;
        double end = data.XValues[^1];
        if (end < 62000 || data.Series.Where(series => series.Name is "Succeeded" or "Failed").Any(series => series.Values[^1] != 0)) throw new Exception("Idle time introduces missing data at the right edge");
        type.GetField("_latestLiveActivity", flags)!.SetValue(page,
            new List<JobHistoryPoint> { new(60000, 0, 1, 1, 0), new(65000, 0, 0, 1, 0) });
        apply.Invoke(page, [totals]);
        if (data.XValues[^1] >= 65000) throw new Exception("A network snapshot changed the steady scroll clock");
        type.GetField("_liveReceivedAt", flags)!.SetValue(page, System.Diagnostics.Stopwatch.GetTimestamp());
        apply.Invoke(page, [totals]);
        if (data.XValues[^1] < end) throw new Exception("An incoming snapshot moved the time window backwards");
        int eventIndex = data.XValues.ToList().IndexOf(60000);
        if (eventIndex < 0 || data.Series[2].Values[eventIndex] != 1) throw new Exception("Advancing the chart lost a real event");
    }

    [Test]
    public void HeaderShowsBusyWorkersAndLinksRunningToHome()
    {
        var header = new FlywheelHeader();
        var totals = new ActivityTotalsState();
        totals.UpdateLive(true);
        totals.UpdateServers(1, 8);
        totals.UpdateRunning(1);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(FlywheelHeader).GetProperty("ActivityTotals", flags)!.SetValue(header, totals);
        var value = typeof(FlywheelHeader).GetMethod("HeaderValue", flags)!;
        if ((string)value.Invoke(header, [5])! != "1/8") throw new Exception("Header does not show busy / capacity");
        totals.UpdateRunning(0);
        if ((string)value.Invoke(header, [5])! != "0/8") throw new Exception("Idle workers are shown as busy");
        var href = typeof(FlywheelHeader).GetMethod("HeaderHref", BindingFlags.Static | BindingFlags.NonPublic)!;
        if ((string)href.Invoke(null, [1])! != "flywheel") throw new Exception("Running does not link home");
    }

    [Test]
    public void SearchDisablesLiveChartAndPreservesEveryState()
    {
        var page = new DashboardPage();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(DashboardPage);
        bool Live() => (bool)type.GetProperty("UseLiveChart", flags)!.GetValue(page)!;
        if (!Live()) throw new Exception("Default chart should be live");
        type.GetField("_query", flags)!.SetValue(page, "report");
        if (Live()) throw new Exception("Search must not use live chart data");
        type.GetField("_historyPoints", flags)!.SetValue(page, new List<JobHistoryPoint>());
        type.GetMethod("ApplySearchHistory", flags)!.Invoke(page, [new List<JobHistoryPoint> { new(300000, 1, 2, 3, 4, 5, 6) }]);
        var series = (ChartSeries[])type.GetField("_activitySeries", flags)!.GetValue(page)!;
        if (series.Length != 7) throw new Exception("Some job states were excluded");
        if (type.GetField("_historyPoints", flags)!.GetValue(page) is not null) throw new Exception("Search must invalidate the aggregate chart cache");
        type.GetField("_query", flags)!.SetValue(page, "");
        if (!Live()) throw new Exception("Clearing search should restore live data");
        type.GetField("_liveMode", flags)!.SetValue(page, false);
        if (Live()) throw new Exception("Date filtering must remain static");
    }
}
