using System.Reflection;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Quark;
using DashboardPage = Soenneker.Flywheel.Dashboard.Pages.Dashboard.Dashboard;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardSearchChartTests
{
    [Test]
    public void GraphRangeFilteringExposesClearAndResetsPaginationWithoutLosingSearchScope()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(DashboardPage);
        var page = new DashboardPage();
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var end = start.AddMinutes(1);
        type.GetField("_offset", flags)!.SetValue(page, 50);
        type.GetField("_query", flags)!.SetValue(page, "maintenance");
        type.GetField("_jobStartAt", flags)!.SetValue(page, start);
        type.GetField("_jobEndAt", flags)!.SetValue(page, end);
        type.GetMethod("ResetFilterPage", flags)!.Invoke(page, null);
        if (!(bool)type.GetProperty("HasFilters", flags)!.GetValue(page)! ||
            (int)type.GetField("_offset", flags)!.GetValue(page)! != 0 ||
            (int)type.GetField("_tableGeneration", flags)!.GetValue(page)! != 1 ||
            (string)type.GetField("_query", flags)!.GetValue(page)! != "maintenance" ||
            (DateTimeOffset)type.GetField("_jobStartAt", flags)!.GetValue(page)! != start ||
            (DateTimeOffset)type.GetField("_jobEndAt", flags)!.GetValue(page)! != end)
            throw new Exception("Graph filtering must expose Clear Filters and reset paging while preserving search and selected time bounds.");
    }

    [Test]
    public void EditingHeaderSelectionKeepsLegendFiltersAndExposesClearFilters()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(DashboardPage);
        var page = new DashboardPage();
        var navigation = new RouterTestNavigationManager("https://example.test/", "https://example.test/?state=Running");
        type.GetProperty("Navigation", flags)!.SetValue(page, navigation);
        type.GetField("_appliedState", flags)!.SetValue(page, "Running");
        var hidden = (HashSet<string>)type.GetField("_hiddenActivitySeries", flags)!.GetValue(page)!;
        hidden.Add("DeadLettered");
        type.GetMethod("ClearHeaderSelection", flags)!.Invoke(page, null);
        if (navigation.Uri.Contains("state=") || !hidden.SetEquals(["DeadLettered"]) ||
            !(bool)type.GetProperty("HasFilters", flags)!.GetValue(page)!)
            throw new Exception("Editing a legend must clear the stale header URL, retain graph filters, and offer Clear Filters.");
    }

    [Test]
    public void HistoricalStatusFiltersDoNotCombineQueuedAndScheduledValues()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(DashboardPage);
        var page = new DashboardPage();
        type.GetProperty("ActivityTotals", flags)!.SetValue(page, new ActivityTotalsState());
        type.GetField("_liveMode", flags)!.SetValue(page, false);
        type.GetMethod("ApplyHistory", flags)!.Invoke(page, [new List<JobHistoryPoint> { new(300000, 11, 12, 13, 14, 15, 16, 17) }]);
        var hidden = (HashSet<string>)type.GetField("_hiddenActivitySeries", flags)!.GetValue(page)!;
        foreach (string state in new[] { "Scheduled", "Running", "Succeeded", "DeadLettered", "Cancelled", "Waiting" }) hidden.Add(state);
        var visible = (IReadOnlyList<ChartSeries>)type.GetProperty("VisibleActivitySeries", flags)!.GetValue(page)!;
        if (visible.Count != 1 || visible[0].Name != "Queued" || visible[0].Values[0] != 17 ||
            !(bool)type.GetProperty("UseMatchingHistory", flags)!.GetValue(page)!)
            throw new Exception("Queued filtering must match queue counts, without showing scheduled activity.");
    }

    [Test]
    public void LaterPagesUseTheirOwnStaticTimeRangeWithoutChangingTableFilters()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var page = new DashboardPage();
        Type type = typeof(DashboardPage);
        type.GetField("_offset", flags)!.SetValue(page, 50);
        type.GetField("_jobs", flags)!.SetValue(page, new List<JobView>
        {
            new("newer", "job", "Succeeded", 1, 940000, false, null, 1, 0, 0, 0, null),
            new("older", "job", "Succeeded", 1, 310000, false, null, 1, 0, 0, 0, null)
        });
        type.GetMethod("SetPageActivityRange", flags)!.Invoke(page, null);
        if ((bool)type.GetProperty("UseLiveChart", flags)!.GetValue(page)! ||
            (bool)type.GetProperty("LiveTable", flags)!.GetValue(page)!)
            throw new Exception("A later table page must not stream live chart or table updates.");
        if (((DateTimeOffset)type.GetField("_pageStartAt", flags)!.GetValue(page)!).ToUnixTimeMilliseconds() != 300000 ||
            ((DateTimeOffset)type.GetField("_pageEndAt", flags)!.GetValue(page)!).ToUnixTimeMilliseconds() != 1200000)
            throw new Exception("Page activity must include the oldest and newest job update buckets.");
        if (type.GetField("_jobStartAt", flags)!.GetValue(page) is not null)
            throw new Exception("Chart bounds must not filter the next table page.");
        type.GetField("_offset", flags)!.SetValue(page, 0);
        if (!(bool)type.GetProperty("UseLiveChart", flags)!.GetValue(page)!)
            throw new Exception("Returning to the unfiltered first page must restore live activity.");
        type.GetField("_liveMode", flags)!.SetValue(page, false);
        if ((bool)type.GetProperty("LiveTable", flags)!.GetValue(page)!)
            throw new Exception("A selected historical timeline must stay static even on its first page.");
    }

    [Test]
    public void EmptyPageClearsThePreviousPageRange()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var page = new DashboardPage();
        typeof(DashboardPage).GetField("_pageStartAt", flags)!.SetValue(page, DateTimeOffset.UtcNow);
        typeof(DashboardPage).GetMethod("SetPageActivityRange", flags)!.Invoke(page, null);
        if (typeof(DashboardPage).GetField("_pageStartAt", flags)!.GetValue(page) is not null)
            throw new Exception("An empty page retained another page's activity range.");
    }

    [Test]
    public void SearchPeriodIsIndependentOfTheCurrentPagesChartBounds()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var page = new DashboardPage();
        Type type = typeof(DashboardPage);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(0);
        var end = start.AddDays(1);
        type.GetField("_query", flags)!.SetValue(page, "maintenance");
        type.GetField("_offset", flags)!.SetValue(page, 50);
        type.GetField("_jobStartAt", flags)!.SetValue(page, start);
        type.GetField("_jobEndAt", flags)!.SetValue(page, end);
        type.GetField("_jobs", flags)!.SetValue(page, new List<JobView>
        {
            new("one", "maintenance", "Succeeded", 1, 610000, false, null, 1, 0, 0, 0, null)
        });
        type.GetMethod("SetPageActivityRange", flags)!.Invoke(page, null);
        if ((DateTimeOffset)type.GetField("_jobStartAt", flags)!.GetValue(page)! != start ||
            (DateTimeOffset)type.GetField("_jobEndAt", flags)!.GetValue(page)! != end)
            throw new Exception("Paging narrowed the full-period job search to the visible chart buckets.");
    }

    [Test]
    public void LiveChartKeepsConstantScrollSpeedWhenTimerTicksSkipBuckets()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var page = new DashboardPage();
        var connection = new DashboardBoardConnection(null!, new ActivityTotalsState());
        typeof(DashboardPage).GetProperty("BoardConnection", flags)!.SetValue(page, connection);
        PropertyInfo optionsProperty = typeof(DashboardPage).GetProperty("ActivityOptions", flags)!;
        var data = connection.LiveActivity.Data;
        var start = DateTimeOffset.UtcNow;
        data.Append(start, 1, 0, 0, 0, 0);
        var initial = (ChartOptions)optionsProperty.GetValue(page)!;
        data.Append(start.AddSeconds(2), 1, 0, 0, 0, 0);
        var delayed = (ChartOptions)optionsProperty.GetValue(page)!;
        if (delayed.RealtimeScrollDuration != TimeSpan.FromSeconds(2))
            throw new Exception("A two-second domain advance must take two seconds to scroll.");
        if (!ReferenceEquals(delayed, optionsProperty.GetValue(page)))
            throw new Exception("An unrelated render reset the animation duration.");
        data.Append(start.AddSeconds(3), 1, 0, 0, 0, 0);
        var regular = (ChartOptions)optionsProperty.GetValue(page)!;
        if (regular.RealtimeScrollDuration != TimeSpan.FromSeconds(1) || regular.Maximum != initial.Maximum)
            throw new Exception("Regular cadence must resume without changing the vertical scale.");
    }

    [Test]
    public void LiveChartRetainsSeriesAndScaleAcrossSamplesAndUnrelatedRenders()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var page = new DashboardPage();
        var connection = new DashboardBoardConnection(null!, new ActivityTotalsState());
        typeof(DashboardPage).GetProperty("BoardConnection", flags)!.SetValue(page, connection);
        PropertyInfo seriesProperty = typeof(DashboardPage).GetProperty("VisibleActivitySeries", flags)!;
        PropertyInfo optionsProperty = typeof(DashboardPage).GetProperty("ActivityOptions", flags)!;
        var data = connection.LiveActivity.Data;
        var start = DateTimeOffset.UtcNow;
        data.Append(start, 10, 3, 1, 0, 2);
        var series = (IReadOnlyList<ChartSeries>)seriesProperty.GetValue(page)!;
        var options = (ChartOptions)optionsProperty.GetValue(page)!;
        if (options.Maximum is not > 10) throw new Exception("Live scale needs headroom above the peak");
        data.Append(start.AddSeconds(1), 11, 4, 1, 0, 2);
        if (!ReferenceEquals(series, seriesProperty.GetValue(page)) || !ReferenceEquals(options, optionsProperty.GetValue(page)))
            throw new Exception("Routine updates replaced the series or scale and interrupted scrolling");
        if (series[0].Values[^1] != 11) throw new Exception("Cached series lost mutable samples");
        data.Clear();
        data.Append(start.AddSeconds(2), 2, 1, 0, 0, 0);
        if (!ReferenceEquals(options, optionsProperty.GetValue(page)))
            throw new Exception("An expired peak shrank the scale during scrolling");
        data.Append(start.AddSeconds(3), 100, 1, 0, 0, 0);
        var expanded = (ChartOptions)optionsProperty.GetValue(page)!;
        if (expanded.Maximum is not >= 100) throw new Exception("New peaks must remain visible");
        var hidden = (HashSet<string>)typeof(DashboardPage).GetField("_hiddenActivitySeries", flags)!.GetValue(page)!;
        hidden.Add("Scheduled");
        typeof(DashboardPage).GetField("_legendVersion", flags)!.SetValue(page, 1L);
        var filtered = (IReadOnlyList<ChartSeries>)seriesProperty.GetValue(page)!;
        if (ReferenceEquals(series, filtered) || filtered.Any(s => s.Name == "Scheduled"))
            throw new Exception("Legend changes did not refresh the visible series");
        if (((ChartOptions)optionsProperty.GetValue(page)!).Maximum >= expanded.Maximum)
            throw new Exception("Explicit legend changes should allow the scale to reset");
    }

    [Test]
    public void LegendExclusionsMapFailedAndScheduledGroupsToTableStatuses()
    {
        var page = new DashboardPage();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var hidden = (HashSet<string>)typeof(DashboardPage).GetField("_hiddenActivitySeries", flags)!.GetValue(page)!;
        hidden.Add("DeadLettered");
        hidden.Add("Scheduled");
        MethodInfo groupedHidden = typeof(DashboardPage).GetMethod("IsActivitySeriesHidden", flags)!;
        if ((bool)groupedHidden.Invoke(page, ["Queued / scheduled"])!)
            throw new Exception("Queued activity must remain visible when only scheduled jobs are hidden");
        hidden.Add("Queued");
        MethodInfo isHidden = typeof(DashboardPage).GetMethod("IsActivitySeriesHidden", flags)!;
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
        List<JobHistoryPoint> points = Enumerable.Range(0, 61).Select(i => new JobHistoryPoint(i * 1000, 0, 0, i % 2, 0)
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
            Id = "job", Name = "job", Payload = "{}", Policy = new JobPolicy(),
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
        Type type = typeof(DashboardLiveActivityState);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        page.Update([new JobHistoryPoint(60000, 0, 1, 1, 0)], totals);
        type.GetField("_liveClockAnchor", flags)!.SetValue(page, 60000L);
        type.GetField("_liveReceivedAt", flags)!.SetValue(page, System.Diagnostics.Stopwatch.GetTimestamp());
        MethodInfo apply = type.GetMethod("Advance")!;
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
        Type type = typeof(DashboardLiveActivityState);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var options = (ChartOptions)typeof(DashboardPage).GetField("LiveActivityOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        string[] colors = ["#8b5cf6", "#0ea5e9", "#10b981", "#f97316", "#6366f1"];
        if (!options.Palette.SequenceEqual(colors)) throw new Exception("Live series lost their status colors");
        if (!options.EnableRealtimeScrolling || options.RealtimeScrollDuration != TimeSpan.FromSeconds(1) || options.Curve != ChartCurve.Monotone) throw new Exception("Live scrolling must interpolate the full one-second sampling interval with curves that do not overshoot");
        var page = new DashboardLiveActivityState();
        var totals = new ActivityTotalsState();
        totals.UpdateLive(true);
        page.Update([new JobHistoryPoint(60000, 0, 1, 1, 0)], totals);
        type.GetField("_liveClockAnchor", flags)!.SetValue(page, 60000L);
        type.GetField("_liveReceivedAt", flags)!.SetValue(page, System.Diagnostics.Stopwatch.GetTimestamp() - 2 * System.Diagnostics.Stopwatch.Frequency);
        MethodInfo apply = type.GetMethod("Advance")!;
        apply.Invoke(page, [totals]);
        var data = (RealtimeChartData)type.GetField("_liveActivity", flags)!.GetValue(page)!;
        double end = data.XValues[^1];
        if (end < 62000 || data.Series.Where(series => series.Name is "Succeeded" or "Failed").Any(series => series.Values[^1] != 0)) throw new Exception("Idle time introduces missing data at the right edge");
        page.Update([new JobHistoryPoint(60000, 0, 1, 1, 0), new JobHistoryPoint(65000, 0, 0, 1, 0)], totals);
        apply.Invoke(page, [totals]);
        if (data.XValues[^1] >= 65000) throw new Exception("A network snapshot changed the steady scroll clock");
        type.GetField("_liveReceivedAt", flags)!.SetValue(page, System.Diagnostics.Stopwatch.GetTimestamp());
        apply.Invoke(page, [totals]);
        if (data.XValues[^1] < end) throw new Exception("An incoming snapshot moved the time window backwards");
        int eventIndex = data.XValues.ToList().IndexOf(60000);
        if (eventIndex < 0 || data.Series[2].Values[eventIndex] != 1) throw new Exception("Advancing the chart lost a real event");
    }

    [Test]
    public void FailedHeaderUsesRetainedTotalAcrossHistoryRanges()
    {
        var totals = new ActivityTotalsState();
        var snapshot = new LiveBoard(1, [], 0,
            [new JobHistoryPoint(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, 0, 5, 0)],
            null, FailedCount: 12);
        totals.UpdateSnapshot(snapshot);
        var header = new FlywheelHeader();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(FlywheelHeader).GetProperty("ActivityTotals", flags)!.SetValue(header, totals);
        MethodInfo value = typeof(FlywheelHeader).GetMethod("HeaderValue", flags)!;
        if ((string)value.Invoke(header, [3])! != "12") throw new Exception("Failed header used recent history instead of retained jobs");
        totals.LastHour = false;
        totals.Update([0, 0, 2]);
        if ((string)value.Invoke(header, [3])! != "12") throw new Exception("Date range overwrote the retained failed total");
        totals.UpdateSnapshot(snapshot with { FailedCount = 0 });
        if ((string)value.Invoke(header, [3])! != "0") throw new Exception("Failed header did not refresh to zero");
        totals.Clear();
        if (totals.FailedCount is not null) throw new Exception("Clearing state retained a stale failed count");
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
        var navigation = new DashboardNavigationOptions();
        typeof(FlywheelHeader).GetProperty("DashboardNavigation", flags)!.SetValue(header, navigation);
        MethodInfo value = typeof(FlywheelHeader).GetMethod("HeaderValue", flags)!;
        if ((string)value.Invoke(header, [5])! != "1/8") throw new Exception("Header does not show busy / capacity");
        totals.UpdateRunning(0);
        if ((string)value.Invoke(header, [5])! != "0/8") throw new Exception("Idle workers are shown as busy");
        MethodInfo href = typeof(FlywheelHeader).GetMethod("HeaderHref", flags)!;
        if ((string)href.Invoke(header, [1])! != "flywheel?state=Running") throw new Exception("Running does not link home with its status selected");
        foreach ((int index, string state) in new[] { (6, "Queued"), (2, "Succeeded"), (3, "DeadLettered") })
            if ((string)href.Invoke(header, [index])! != $"flywheel?state={state}")
                throw new Exception($"{state} does not link to its dashboard filter");
        navigation.HomePath = "/jobs";
        if ((string)href.Invoke(header, [1])! != "jobs?state=Running") throw new Exception("Running does not link to the configured home");
        navigation.HomePath = "/";
        if ((string)href.Invoke(header, [1])! != "./?state=Running") throw new Exception("Running does not link to the application root");
    }

    [Test]
    public void SearchDisablesLiveChartAndPreservesEveryState()
    {
        var page = new DashboardPage();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        Type type = typeof(DashboardPage);
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
