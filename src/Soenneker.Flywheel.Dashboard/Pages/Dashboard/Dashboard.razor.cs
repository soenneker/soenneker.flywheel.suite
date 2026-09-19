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
    private bool HasFilters => _hiddenActivitySeries.Count > 0 || _query.Length > 0 || !_liveMode || _jobStartAt is not null || _activityPaused;
    private bool ActivityLoading => _historyLoading || (UseLiveChart && !_loaded);
    private CancellationTokenSource? _activeRead;
    private readonly DataTableOptions _tableOptions = new() { DefaultPageSize = 50, SearchDebounceMs = 300 };

    protected override void OnInitialized()
    {
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

    protected override async Task OnParametersSetAsync()
    {
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
        _offset = 0;
        _queryRevision = BoardConnection.NextVersion();
        if (_activeRead is { } activeRead) await activeRead.CancelAsync();
        if (_jobsTable is not null) await _jobsTable.GoToPage(1);
        await Task.WhenAll(firstLoad ? LoadOverviewHistory() : Task.CompletedTask, Reload());
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
            await BoardConnection.Configure(revision, _query, _offset, _pageSize, _jobStartAt, _jobEndAt, read.Token, excludedStates);
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception)
        {
            if (revision == _queryRevision) _error = "Could not load executions. Retry to reconnect.";
        }
        finally
        {
            if (ReferenceEquals(_activeRead, read)) _activeRead = null;
            if (revision == _queryRevision) _loading = false;
            if (!IsDisposed && !IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }

    private Task OnSnapshot(LiveBoard snapshot) => InvokeAsync(() =>
    {
        if (IsDisposed || IsCancellationRequested || snapshot.Version != _queryRevision) return;
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

    private async Task ToggleActivitySeries(string name)
    {
        bool hidden = IsActivitySeriesHidden(name);
        foreach (string state in SeriesStates(name))
            if (hidden) _hiddenActivitySeries.Remove(state);
            else _hiddenActivitySeries.Add(state);
        _legendVersion++;
        _offset = 0;
        if (_jobsTable is not null) await _jobsTable.GoToPage(1);
        _queryRevision = BoardConnection.NextVersion();
        if (_activeRead is { } activeRead) await activeRead.CancelAsync();
        await Reload();
    }

    private static readonly string[] ActivityColors = [JobStatusColors.Accent("Scheduled"), JobStatusColors.Accent("Succeeded"), JobStatusColors.Accent("DeadLettered")];
    private long _activityVersion;
    private int _historyRevision;
    private Chart? _activityChart;
    private DashboardActivityRegion? _activityRegion;
    private bool _activityPaused;
    private RealtimeChartData _liveActivity => BoardConnection.LiveActivity.Data;
    private Task? _liveClock;
    private double[] _activityTotals = [0, 0, 0];
    private string[] _activityLabels = [];
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
                return mobile ? !string.IsNullOrWhiteSpace(_query) ? MobileSearchActivityOptions : MobileHistoryActivityOptions
                    : !string.IsNullOrWhiteSpace(_query) ? SearchActivityOptions : HistoryActivityOptions;

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
        LabelFormatter = label => live && label.Length > 8 ? label[..8] : label,
        Palette = [JobStatusColors.Accent("Scheduled"), JobStatusColors.Accent("Running"),
            JobStatusColors.Accent("Succeeded"), JobStatusColors.Accent("DeadLettered"), JobStatusColors.Accent("Queued")]
    };
    private DateTimeOffset? _jobStartAt;
    private DateTimeOffset? _jobEndAt;
    private string? _historyError;
    private bool _historyLoading;
    private bool _liveMode = true;
    private bool UseLiveChart => _liveMode && string.IsNullOrWhiteSpace(_query);
    private string DateRangeLabel => _jobStartAt is { } start && _jobEndAt is { } end
        ? $"{start:MMM d HH:mm:ss}–{end:MMM d HH:mm:ss} UTC"
        : _liveMode ? (UseLiveChart ? "Live" : "All dates") : $"{_historyStartDate:MMM d}–{_historyEndDate:MMM d} UTC";
    private int _historyRetentionDays = 1;
    private DateOnly _historyStartDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateOnly _historyEndDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateOnly HistoryMaxDate => DateOnly.FromDateTime(DateTime.UtcNow);
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
        if (!_liveMode && _historyStartDate == from && _historyEndDate == to) return;
        _liveMode = false;
        ActivityTotals.LastHour = false;
        _historyStartDate = from;
        _historyEndDate = to;
        await ClearActivitySelection();
    }

    private async Task ShowLive()
    {
        _liveMode = true;
        ActivityTotals.LastHour = true;
        await ClearActivitySelection();
    }

    private void ApplyLiveActivity() => BoardConnection.LiveActivity.Advance(ActivityTotals);

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
                    ApplyLiveActivity();
                    _activityRegion?.Refresh();
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ReloadHistory(CancellationToken cancellationToken)
    {
        int revision = ++_historyRevision;
        _historyLoading = !UseLiveChart;
        _historyError = null;
        StateHasChanged();
        if (!string.IsNullOrWhiteSpace(_query))
        {
            try
            {
                _historyError = null;
                _activitySeries = [];
                _activityLabels = [];
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
            DateTimeOffset startAt = new(_historyStartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            DateTimeOffset requestedEnd = new(_historyEndDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
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
        _activityLabels = points.Select(p => DateTimeOffset.FromUnixTimeMilliseconds(p.Timestamp).ToString("MMM d HH:mm")).ToArray();
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
        _historyPoints = points;
        _activityXValues = points.Select(point => (double)point.Timestamp).ToArray();
        _activityLabels = points.Select(point => DateTimeOffset.FromUnixTimeMilliseconds(point.Timestamp).ToString("MMM d HH:mm")).ToArray();
        double[][] values = [points.Select(p => (double)p.Scheduled).ToArray(),
            points.Select(p => (double)p.Succeeded).ToArray(), points.Select(p => (double)p.DeadLettered).ToArray()];
        string[] labels = ["Queued / scheduled", "Succeeded", "Failed"];
        _activityTotals = values.Select(series => series.Sum()).ToArray();
        ActivityTotals.Update(_activityTotals);
        _activityVersion++;
        _activitySeries = labels.Select((label, index) => new ChartSeries(label, values[index]) { Color = ActivityColors[index] }).ToArray();
        _historyError = null;
    }

    private async Task SelectActivityRange(ChartRangeSelection selection)
    {
        if (selection.StartXValue is not { } start || selection.EndXValue is not { } end) return;
        _jobStartAt = DateTimeOffset.FromUnixTimeMilliseconds((long)start);
        _jobEndAt = DateTimeOffset.FromUnixTimeMilliseconds((long)end).Add(UseLiveChart ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(5));
        _offset = 0;
        _queryRevision = BoardConnection.NextVersion();
        if (_activeRead is { } activeRead) await activeRead.CancelAsync();
        await Task.WhenAll(Reload(), !string.IsNullOrWhiteSpace(_query) ? ReloadHistory(CancellationToken) : Task.CompletedTask);
    }

    private async Task ClearActivitySelection()
    {
        _jobStartAt = _liveMode ? null : new DateTimeOffset(_historyStartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        _jobEndAt = _liveMode ? null : new DateTimeOffset(_historyEndDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
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
        if (_loaded && _query == (request.Search?.Value ?? "") && _offset == request.Start && _pageSize == request.Length) return;
        bool queryChanged = _query != (request.Search?.Value ?? "");
        _query = request.Search?.Value ?? "";
        if (queryChanged)
        {
            _activityPaused = false;
            _activityChart?.ResetZoom();
        }
        _offset = request.Start;
        _pageSize = request.Length;
        _queryRevision = BoardConnection.NextVersion();
        if (_activeRead is { } activeRead) await activeRead.CancelAsync();
        if (_query.Length > 200)
        {
            _jobs.Clear();
            _totalCount = 0;
            _error = "Search must be 200 characters or fewer.";
            return;
        }

        await Task.WhenAll(Reload(), queryChanged ? ReloadHistory(CancellationToken) : Task.CompletedTask);
    }

    public override async ValueTask DisposeAsync()
    {
        BoardConnection.Snapshot -= OnSnapshot;
        await base.DisposeAsync();
        if (_liveClock is not null) await _liveClock;
        await BoardConnection.ReleaseQuery(_queryRevision);
    }
}
