using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Soenneker.Quark;
using Soenneker.DataTables.Dtos.ServerSideRequest;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Dashboard.Communication;
using System.Net;
using Soenneker.Dtos.Results.Operation;

namespace Soenneker.Flywheel.Dashboard.Pages.Dashboard;

public partial class Dashboard
{
    private JobDetailsDrawer? _jobDrawer;
    private Task ShowJobDrawer(JobView job) => _jobDrawer?.Show(job) ?? Task.CompletedTask;
    private Task OpenJobDrawer(KeyboardEventArgs e, JobView job) => e.Key is "Enter" or " " ? ShowJobDrawer(job) : Task.CompletedTask;
    private List<JobView> _jobs = [];
    private string? _error;
    private bool _loading = true, _loaded;
    private int _offset, _totalCount, _queryRevision;
    private int _pageSize = 50;
    private string _query = "";
    private int _tableGeneration;
    private bool HasFilters => _offset > 0 || _hiddenActivitySeries.Count > 0 || _query.Length > 0 || !_liveMode || _jobStartAt is not null || _activityPaused;
    private bool LiveTable => _offset == 0 && _liveMode && _jobStartAt is null && string.IsNullOrWhiteSpace(_query);
    private bool ActivityLoading => _historyLoading || (UseLiveChart && !_loaded);
    private CancellationTokenSource? _activeRead;
    private readonly DataTableOptions _tableOptions = new() { DefaultPageSize = 50, SearchDebounceMs = 300 };

    protected override void OnInitialized()
    {
        _historyStartDate = _historyEndDate = TimeZone.Today;
        _queryRevision = BoardConnection.NextVersion();
        ActivityTotals.LastHour = true;
        BoardConnection.Snapshot += OnSnapshot;
        ApplyLiveActivity();
        _liveClock = AdvanceLiveChart();
    }

    /// <summary>The job status to select in the activity graph and execution table.</summary>
    [Parameter] public string? SelectedState { get; set; }

    private static readonly string[] FilterStates = ["Scheduled", "Queued", "Running", "Succeeded", "DeadLettered", "Cancelled", "Waiting"];
    private bool _parametersInitialized;
    private string? _appliedState;
    private string? _appliedTimeZone;

    protected override async Task OnParametersSetAsync()
    {
        bool timezoneChanged = _appliedTimeZone is not null && _appliedTimeZone != DisplayTimeZone;
        _appliedTimeZone = DisplayTimeZone;
        if (timezoneChanged)
        {
            if (_liveMode) _historyStartDate = _historyEndDate = TimeZone.Today;
            else
            {
                _historyEndDate = _historyEndDate > HistoryMaxDate ? HistoryMaxDate : _historyEndDate;
                _historyStartDate = _historyStartDate > _historyEndDate ? _historyEndDate : _historyStartDate;
                await ClearActivitySelection();
            }
        }
        string? selected = FilterStates.FirstOrDefault(state => string.Equals(state, SelectedState, StringComparison.OrdinalIgnoreCase));
        if (_parametersInitialized && selected == _appliedState) return;
        bool firstLoad = !_parametersInitialized;
        _parametersInitialized = true;
        _appliedState = selected;
        _hiddenActivitySeries.Clear();
        if (selected is not null)
            foreach (string state in FilterStates)
                if (state != selected) _hiddenActivitySeries.Add(state);
        _legendVersion++;
        ResetFilterPage();
        _queryRevision = BoardConnection.NextVersion();
        if (_activeRead is { } activeRead) await activeRead.CancelAsync();
        await Task.WhenAll(firstLoad ? LoadOverviewHistory() : ReloadHistory(CancellationToken), Reload());
    }

    private async Task LoadOverviewHistory()
    {
        try
        {
            await LoadHistoryOptions(CancellationToken);
            await ReloadHistory(CancellationToken);
        }
        catch (OperationCanceledException) when (IsCancellationRequested) { }
        catch (Exception) { _historyError = "Unable to load job history. Reload the page to retry."; }
    }

    private async Task Reload()
    {
        if (_query.Length > 200) return;
        _loading = true;
        StateHasChanged();
        if (_activeRead is { } activeRead) await activeRead.CancelAsync();
        using var read = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        _activeRead = read;
        int revision = _queryRevision;
        string excludedStates = ExcludedStates;
        try
        {
            OperationResult<SearchResult> response = await Consumer.Search(_query, _offset, _pageSize, _jobStartAt, _jobEndAt, read.Token, excludedStates);
            if (response.StatusCode == (int)HttpStatusCode.Unauthorized) { Session.SetAuthenticated(false); return; }
            response.EnsureSucceeded();
            if (revision != _queryRevision) return;
            _jobs = (response.Value?.Items ?? []).Where(job => !_hiddenActivitySeries.Contains(job.State)).ToList();
            _totalCount = response.Value?.TotalCount ?? 0;
            _loaded = true;
            _loading = false;
            _error = null;
            // Historical pages are one-shot reads. Keep the shared header connected,
            // but do not stream replacement pages into a historical table.
            await BoardConnection.Configure(revision, LiveTable ? _query : "", 0, LiveTable ? _pageSize : 1,
                null, null, read.Token, LiveTable ? excludedStates : null);
            if (_offset > 0) await ReloadPageHistory(revision, read.Token);
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception)
        {
            if (revision == _queryRevision) _error = "Could not load executions. Retry to reconnect.";
        }
        finally
        {
            if (ReferenceEquals(_activeRead, read)) _activeRead = null;
            if (revision == _queryRevision)
            {
                _loading = false;
                if (_offset > 0) _historyLoading = false;
            }
            if (!IsDisposed && !IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }

    private Task OnSnapshot(LiveBoard snapshot) => InvokeAsync(() =>
    {
        if (IsDisposed || IsCancellationRequested || snapshot.Version != _queryRevision || !LiveTable) return;
        bool changed = _loading || !_loaded || _error is not null || _totalCount != snapshot.TotalCount || !_jobs.SequenceEqual(snapshot.Items);
        _jobs = snapshot.Items.Where(job => !_hiddenActivitySeries.Contains(job.State)).ToList();
        _totalCount = snapshot.TotalCount;
        _loading = false;
        _loaded = true;
        _error = null;
        if (_liveActivity.XValues.Count == 0 && UseLiveChart && !_activityPaused)
        {
            ApplyLiveActivity();
            changed = true;
        }
        if (changed) StateHasChanged();
    });

    private DataTable? _jobsTable;
    private readonly HashSet<string> _hiddenActivitySeries = [];
    private long _legendVersion;
    private IReadOnlyList<ChartSeries> ActivityLegendSeries => UseLiveChart ? _liveActivity.Series : _activitySeries;
    private static string[] SeriesStates(string name) => name switch
    {
        "Failed" => ["DeadLettered"],
        "Queued / scheduled" => ["Queued", "Scheduled"],
        _ => [name]
    };
    private bool IsActivitySeriesHidden(string name) => SeriesStates(name).All(_hiddenActivitySeries.Contains);
    private string ExcludedStates => string.Join(',', _hiddenActivitySeries);
    private string ActivitySeriesColor(ChartSeries series) => series.Color ?? JobStatusColors.Accent(SeriesStates(series.Name)[0]);
    private IReadOnlyList<ChartSeries>? _visibleActivitySource;
    private long _visibleLegendVersion = -1;
    private IReadOnlyList<ChartSeries> _visibleActivitySeries = [];
    private IReadOnlyList<ChartSeries> VisibleActivitySeries
    {
        get
        {
            IReadOnlyList<ChartSeries> source = ActivityLegendSeries;
            if (!ReferenceEquals(source, _visibleActivitySource) || _visibleLegendVersion != _legendVersion)
            {
                _visibleActivitySource = source;
                _visibleLegendVersion = _legendVersion;
                _visibleActivitySeries = source.Where(series => !IsActivitySeriesHidden(series.Name))
                    .Select(series => new ChartSeries(series.Name, series.Values) { Color = ActivitySeriesColor(series) }).ToArray();
                _liveMaximum = 1;
                _scaledLiveOptions = null;
                _scaledMobileLiveOptions = null;
            }
            return _visibleActivitySeries;
        }
    }

    private async Task ClearFilters()
    {
        _query = "";
        _hiddenActivitySeries.Clear();
        _legendVersion++;
        _appliedState = null;
        _liveMode = true;
        ActivityTotals.LastHour = true;
        _tableGeneration++;
        _jobsTable = null;
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("state", (string?)null));
        await ClearActivitySelection();
    }

    private void ResetFilterPage()
    {
        if (_offset > 0) { _tableGeneration++; _jobsTable = null; }
        _offset = 0;
        _pageStartAt = _pageEndAt = null;
    }

    private void ClearHeaderSelection()
    {
        // A manually edited legend is no longer the header's single-status filter.
        // Update the applied parameter before navigation so it does not clear the legend.
        if (_appliedState is null) return;
        _appliedState = null;
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("state", (string?)null), replace: true);
    }

    private async Task ToggleActivitySeries(string name)
    {
        bool hidden = IsActivitySeriesHidden(name);
        foreach (string state in SeriesStates(name))
            if (hidden) _hiddenActivitySeries.Remove(state);
            else _hiddenActivitySeries.Add(state);
        _legendVersion++;
        ClearHeaderSelection();
        ResetFilterPage();
        _queryRevision = BoardConnection.NextVersion();
        if (_activeRead is { } activeRead) await activeRead.CancelAsync();
        await Task.WhenAll(Reload(), ReloadHistory(CancellationToken));
    }

    private long _activityVersion;
    private (bool Live, long Version, string TimeZone)? _activityLabelsKey;
    private string[] _activityLabels = [];
    private string[] ActivityLabels
    {
        get
        {
            var key = (UseLiveChart, UseLiveChart ? _liveActivity.Version : _activityVersion, DisplayTimeZone);
            if (_activityLabelsKey != key)
            {
                _activityLabels = TimeZone.ChartLabels(UseLiveChart ? _liveActivity.XValues : _activityXValues, UseLiveChart);
                _activityLabelsKey = key;
            }
            return _activityLabels;
        }
    }
    private int _historyRevision;
    private Chart? _activityChart;
    private DashboardActivityRegion? _activityRegion;
    private bool _activityPaused;
    private RealtimeChartData _liveActivity => BoardConnection.LiveActivity.Data;
    private Task? _liveClock;
    private double[] _activityTotals = [0, 0, 0];
    private double[] _activityXValues = [];
    private ChartSeries[] _activitySeries = [];
    [CascadingParameter(Name = "SidebarContextState")]
    public Soenneker.Quark.Dtos.SidebarContextState? SidebarState { get; set; }

    private static readonly ChartOptions LiveActivityOptions = CreateActivityOptions(live: true);
    private static readonly ChartOptions SearchActivityOptions = CreateActivityOptions(search: true);
    private static readonly ChartOptions HistoryActivityOptions = CreateActivityOptions();
    private static readonly ChartOptions MobileLiveActivityOptions = CreateActivityOptions(mobile: true, live: true);
    private static readonly ChartOptions MobileSearchActivityOptions = CreateActivityOptions(mobile: true, search: true);
    private static readonly ChartOptions MobileHistoryActivityOptions = CreateActivityOptions(mobile: true);
    private double _liveMaximum = 1;
    private double? _renderedLiveEnd;
    private TimeSpan _liveScrollDuration = TimeSpan.FromSeconds(1);
    private ChartOptions? _scaledLiveOptions;
    private ChartOptions? _scaledMobileLiveOptions;
    private ChartOptions ActivityOptions
    {
        get
        {
            bool mobile = SidebarState?.IsMobile == true;
            if (!UseLiveChart)
                return mobile ? UseMatchingHistory ? MobileSearchActivityOptions : MobileHistoryActivityOptions
                    : UseMatchingHistory ? SearchActivityOptions : HistoryActivityOptions;

            // Match animation time to the domain advance, not the nominal timer period.
            // A late tick can skip a bucket; scrolling it in one second doubles the speed.
            if (_liveActivity.XValues.Count > 0)
            {
                double end = _liveActivity.XValues[^1];
                if (_renderedLiveEnd is { } previous && end > previous)
                {
                    TimeSpan duration = TimeSpan.FromMilliseconds(end - previous);
                    if (duration != _liveScrollDuration)
                    {
                        _liveScrollDuration = duration;
                        _scaledLiveOptions = null;
                        _scaledMobileLiveOptions = null;
                    }
                }
                _renderedLiveEnd = end;
            }

            // Keep headroom and retain the scale as peaks leave the window. Repeated
            // auto-scaling cancels horizontal scrolling and makes every sample jump.
            double peak = 0;
            foreach (ChartSeries series in VisibleActivitySeries)
                foreach (double? value in series.Values)
                    if (value is { } number && number > peak) peak = number;
            if (peak > _liveMaximum)
            {
                _liveMaximum = Math.Pow(2, Math.Ceiling(Math.Log2(peak * 1.25)));
                _scaledLiveOptions = null;
                _scaledMobileLiveOptions = null;
            }
            return mobile
                ? _scaledMobileLiveOptions ??= CreateActivityOptions(mobile: true, live: true, maximum: _liveMaximum, scrollDuration: _liveScrollDuration)
                : _scaledLiveOptions ??= CreateActivityOptions(live: true, maximum: _liveMaximum, scrollDuration: _liveScrollDuration);
        }
    }

    private static ChartOptions CreateActivityOptions(bool mobile = false, bool live = false, bool search = false, double? maximum = null, TimeSpan? scrollDuration = null) => new()
    {
        Legend = ChartLegendPosition.None, Width = mobile ? 360 : 1200, Height = mobile ? 220 : 140,
        ShowYAxis = mobile, ShowGrid = mobile, PaddingLeft = mobile ? 40 : 24, ClipPlot = true,
        ShowPoints = search, Animate = false, EnableRangeSelection = true, PauseOnRangeSelection = !search,
        EnableRealtimeScrolling = live, RealtimeScrollDuration = scrollDuration ?? TimeSpan.FromSeconds(1),
        Curve = ChartCurve.Monotone, Minimum = 0, Maximum = maximum, MaximumXAxisLabels = mobile ? 3 : live ? 7 : 12,
        LabelFormatter = label => label,
        Palette = [JobStatusColors.Accent("Scheduled"), JobStatusColors.Accent("Running"),
            JobStatusColors.Accent("Succeeded"), JobStatusColors.Accent("DeadLettered"), JobStatusColors.Accent("Queued")]
    };
    private DateTimeOffset? _jobStartAt;
    private DateTimeOffset? _jobEndAt;
    private string? _historyError;
    private bool _historyLoading;
    private bool _liveMode = true;
    private DateTimeOffset? _pageStartAt, _pageEndAt;
    private bool UseLiveChart => _offset == 0 && _liveMode && string.IsNullOrWhiteSpace(_query);
    private bool UseMatchingHistory => !string.IsNullOrWhiteSpace(_query) || _hiddenActivitySeries.Count > 0;
    private string DateRangeLabel => _offset > 0
        ? _pageStartAt is { } pageStart && _pageEndAt is { } pageEnd
            ? $"{TimeZone.Format(pageStart, "MMM d HH:mm")}–{TimeZone.Format(pageEnd, "MMM d HH:mm")}" : "Page activity"
        : _jobStartAt is { } start && _jobEndAt is { } end
        ? $"{TimeZone.Format(start, "MMM d HH:mm:ss")}–{TimeZone.Format(end, "MMM d HH:mm:ss")}"
        : _liveMode ? (UseLiveChart ? "Live" : "All dates") : $"{_historyStartDate:MMM d}–{_historyEndDate:MMM d} {TimeZone.Id}";
    private int _historyRetentionDays = 1;
    private DateOnly _historyStartDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateOnly _historyEndDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateOnly HistoryMaxDate => TimeZone.Today;
    private DateOnly HistoryMinDate => HistoryMaxDate.AddDays(-_historyRetentionDays + 1);
    private IReadOnlyList<PresetDateRangePickerOption> HistoryRangePresets =>
        new[] { 1, 3, 7, 14, 30 }
            .Where(days => days <= _historyRetentionDays)
            .Select(days => PresetDateRangePickerOption.LastDays(days, days == 1 ? "Today" : null))
            .ToArray();

    private async Task LoadHistoryOptions(CancellationToken cancellationToken)
    {
        OperationResult<HistoryOptions> response = await Consumer.GetHistoryOptions(cancellationToken);
        if (response.StatusCode == (int)HttpStatusCode.NotImplemented) return;
        response.EnsureSucceeded();
        HistoryOptions options = response.Value
                                 ?? throw new InvalidOperationException("Missing history options response.");
        _historyRetentionDays = Math.Max(1, (int)Math.Ceiling(options.RetentionSeconds / 86400d));
        _historyStartDate = HistoryMinDate > _historyStartDate ? HistoryMinDate : _historyStartDate;
    }

    private async Task SetHistoryRange(CalendarDateRange? range)
    {
        if (range?.From is not { } from || range.To is not { } to || from > to) return;
        from = from < HistoryMinDate ? HistoryMinDate : from;
        to = to > HistoryMaxDate ? HistoryMaxDate : to;
        if (to.DayNumber - from.DayNumber + 1 > _historyRetentionDays) return;
        if (_offset == 0 && !_liveMode && _historyStartDate == from && _historyEndDate == to) return;
        _liveMode = false;
        ActivityTotals.LastHour = false;
        _historyStartDate = from;
        _historyEndDate = to;
        await ClearActivitySelection();
    }

    private async Task ShowLive()
    {
        if (_query.Length > 0) { _query = ""; _tableGeneration++; _jobsTable = null; }
        _liveMode = true;
        ActivityTotals.LastHour = true;
        await ClearActivitySelection();
    }

    private void ApplyLiveActivity() => BoardConnection.LiveActivity.Advance(ActivityTotals);

    private void SetPageActivityRange()
    {
        _pageStartAt = _pageEndAt = null;
        if (_jobs.Count == 0) return;
        // Table date filters use UpdatedAt. Include the complete five-minute
        // history buckets containing both ends of this page, independent of order.
        const long bucket = 300000;
        _pageStartAt = DateTimeOffset.FromUnixTimeMilliseconds(_jobs.Min(job => job.UpdatedAt) / bucket * bucket);
        _pageEndAt = DateTimeOffset.FromUnixTimeMilliseconds((_jobs.Max(job => job.UpdatedAt) / bucket + 1) * bucket);
    }

    private async Task ReloadPageHistory(int revision, CancellationToken cancellationToken)
    {
        SetPageActivityRange();
        _historyError = null;
        _historyLoading = true;
        _historyPoints = null;
        _activitySeries = [];
        _activityXValues = [];
        _activityVersion++;
        StateHasChanged();
        try
        {
            if (_pageStartAt is not { } start || _pageEndAt is not { } end) return;
            bool matching = UseMatchingHistory;
            var response = matching
                ? await Consumer.GetSearchHistory(_query, start, end, cancellationToken)
                : await Consumer.GetHistory(start, end, cancellationToken);
            response.EnsureSucceeded();
            if (revision != _queryRevision || _offset == 0) return;
            if (matching) ApplySearchHistory(response.Value ?? []);
            else ApplyHistory(response.Value ?? []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            if (revision == _queryRevision) _historyError = "Activity for this page is unavailable or outside retained history.";
        }
        finally
        {
            if (revision == _queryRevision) _historyLoading = false;
        }
    }

    private async Task AdvanceLiveChart()
    {
        CancellationToken cancellationToken = CancellationToken;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await InvokeAsync(() =>
                {
                    if (!UseLiveChart || _activityPaused || !ActivityTotals.Live || IsDisposed) return;
                    long previousVersion = _liveActivity.Version;
                    ApplyLiveActivity();
                    if (_liveActivity.Version != previousVersion) _activityRegion?.Refresh();
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ReloadHistory(CancellationToken cancellationToken)
    {
        if (_offset > 0) return; // Loaded after the requested table page establishes its timestamps.
        int revision = ++_historyRevision;
        _historyLoading = !UseLiveChart;
        _historyError = null;
        StateHasChanged();
        if (!UseLiveChart && UseMatchingHistory)
        {
            try
            {
                _historyError = null;
                _activitySeries = [];
                _activityXValues = [];
                _activityVersion++;
                OperationResult<List<JobHistoryPoint>> response = await Consumer.GetSearchHistory(_query, _jobStartAt, _jobEndAt, cancellationToken);
                response.EnsureSucceeded();
                if (revision == _historyRevision) ApplySearchHistory(response.Value ?? []);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { if (revision == _historyRevision) _historyError = "Unable to load matching job activity. Try the search again."; }
            finally
            {
                if (revision == _historyRevision) _historyLoading = false;
                if (!IsDisposed) await InvokeAsync(StateHasChanged);
            }
            return;
        }
        if (_liveMode) { _historyError = null; return; }
        try
        {
            _historyLoading = true;
            DateTimeOffset startAt = TimeZone.StartOfDay(_historyStartDate);
            DateTimeOffset requestedEnd = TimeZone.StartOfDay(_historyEndDate.AddDays(1));
            DateTimeOffset endAt = requestedEnd > DateTimeOffset.UtcNow ? DateTimeOffset.UtcNow : requestedEnd;
            OperationResult<List<JobHistoryPoint>> response = await Consumer.GetHistory(startAt, endAt, cancellationToken);
            response.EnsureSucceeded();
            List<JobHistoryPoint> points = response.Value
                                           ?? throw new InvalidOperationException("Missing history response.");
            if (revision == _historyRevision && !_liveMode && !_activityPaused && string.IsNullOrWhiteSpace(_query)) ApplyHistory(points);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            if (revision == _historyRevision) _historyError = "Unable to load job history. Reload the page to retry.";
        }
        finally
        {
            if (revision == _historyRevision) _historyLoading = false;
            if (!IsDisposed) await InvokeAsync(StateHasChanged);
        }
    }

    private void ApplySearchHistory(List<JobHistoryPoint> points)
    {
        _historyPoints = null;
        _historyError = null;
        _activityXValues = points.Select(p => (double)p.Timestamp).ToArray();
        string[] states = ["Scheduled", "Running", "Succeeded", "DeadLettered", "Cancelled", "Waiting", "Queued"];
        double[][] values = [points.Select(p => (double)p.Scheduled).ToArray(), points.Select(p => (double)p.Running).ToArray(),
            points.Select(p => (double)p.Succeeded).ToArray(), points.Select(p => (double)p.DeadLettered).ToArray(),
            points.Select(p => (double)p.Cancelled).ToArray(), points.Select(p => (double)p.Waiting).ToArray(), points.Select(p => (double)p.Queued).ToArray()];
        _activitySeries = states.Select((state, i) => new ChartSeries(state == "DeadLettered" ? "Failed" : state, values[i])
            { Color = JobStatusColors.Accent(state) }).ToArray();
        _activityVersion++;
    }

    private List<JobHistoryPoint>? _historyPoints;
    private void ApplyHistory(List<JobHistoryPoint> points)
    {
        _historyError = null;
        if (_historyPoints is not null && _historyPoints.SequenceEqual(points)) return;
        _activityTotals = [points.Sum(p => (double)p.Scheduled), points.Sum(p => (double)p.Succeeded), points.Sum(p => (double)p.DeadLettered)];
        ActivityTotals.Update(_activityTotals);
        ApplySearchHistory(points);
        _historyPoints = points;
        _historyError = null;
    }

    private async Task SelectActivityRange(ChartRangeSelection selection)
    {
        if (selection.StartXValue is not { } start || selection.EndXValue is not { } end) return;
        _jobStartAt = DateTimeOffset.FromUnixTimeMilliseconds((long)start);
        _jobEndAt = DateTimeOffset.FromUnixTimeMilliseconds((long)end).Add(UseLiveChart ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(5));
        if (_offset > 0)
        {
            _liveMode = false;
            ActivityTotals.LastHour = false;
            _historyStartDate = DateOnly.FromDateTime(TimeZone.Convert(_jobStartAt.Value).DateTime);
            _historyEndDate = DateOnly.FromDateTime(TimeZone.Convert(_jobEndAt.Value).DateTime);
        }
        ResetFilterPage();
        _queryRevision = BoardConnection.NextVersion();
        if (_activeRead is { } activeRead) await activeRead.CancelAsync();
        await Task.WhenAll(Reload(), !UseLiveChart && UseMatchingHistory ? ReloadHistory(CancellationToken) : Task.CompletedTask);
    }

    private async Task ClearActivitySelection()
    {
        ResetFilterPage();
        _jobStartAt = _liveMode ? null : TimeZone.StartOfDay(_historyStartDate);
        _jobEndAt = _liveMode ? null : TimeZone.StartOfDay(_historyEndDate.AddDays(1));
        _activityChart?.ResetZoom();
        _activityPaused = false;
        if (_liveMode) ApplyLiveActivity();
        _offset = 0;
        _queryRevision = BoardConnection.NextVersion();
        if (_activeRead is { } activeRead) await activeRead.CancelAsync();
        await Task.WhenAll(ReloadHistory(CancellationToken), Reload());
    }

    private readonly HashSet<string> _cancellingJobs = [];

    private async Task Cancel(string id)
    {
        if (!_cancellingJobs.Add(id)) return;
        try
        {
            OperationResult<object> response = await Consumer.CancelJob(id, CancellationToken);
            if (response.StatusCode == (int)HttpStatusCode.Unauthorized) { Session.SetAuthenticated(false); return; }
            if (response.StatusCode == (int)HttpStatusCode.Conflict)
            {
                await Reload();
                _error = "This execution can no longer be cancelled. Its status may have changed.";
                return;
            }
            response.EnsureSucceeded();
            await Reload();
        }
        catch (Exception)
        {
            _error = "Cancellation was not confirmed. Retry if the job is still active.";
        }
        finally { _cancellingJobs.Remove(id); }
    }

    private async Task SearchJobs(DataTableServerSideRequest request)
    {
        if ((_loaded || _loading) && _query == (request.Search?.Value ?? "") && _offset == request.Start && _pageSize == request.Length) return;
        bool queryChanged = _query != (request.Search?.Value ?? "");
        bool pageChanged = _offset != request.Start;
        _query = request.Search?.Value ?? "";
        if (queryChanged)
        {
            _activityPaused = false;
            _activityChart?.ResetZoom();
        }
        _offset = request.Start;
        _pageSize = request.Length;
        _historyLoading = false;
        if (pageChanged && UseLiveChart) ApplyLiveActivity();
        if (_offset > 0)
        {
            ++_historyRevision; // Invalidate an earlier timeline request.
            _activityPaused = false;
            _activityChart?.ResetZoom();
            _historyLoading = true;
            _historyError = null;
            _pageStartAt = _pageEndAt = null;
            _activitySeries = [];
            _activityXValues = [];
            _historyPoints = null;
            _activityVersion++;
        }
        _queryRevision = BoardConnection.NextVersion();
        if (_activeRead is { } activeRead) await activeRead.CancelAsync();
        if (_query.Length > 200)
        {
            _jobs.Clear();
            _totalCount = 0;
            _error = "Search must be 200 characters or fewer.";
            return;
        }

        await Task.WhenAll(Reload(), _offset == 0 && (queryChanged || !_liveMode || pageChanged && !string.IsNullOrWhiteSpace(_query))
            ? ReloadHistory(CancellationToken) : Task.CompletedTask);
    }

    public override async ValueTask DisposeAsync()
    {
        BoardConnection.Snapshot -= OnSnapshot;
        await base.DisposeAsync();
        if (_liveClock is not null) await _liveClock;
        await BoardConnection.ReleaseQuery(_queryRevision);
    }
}
