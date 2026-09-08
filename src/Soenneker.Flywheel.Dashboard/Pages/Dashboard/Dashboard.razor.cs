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

    protected override Task OnInitializedAsync() => Task.WhenAll(LoadOverviewHistory(), Reload());

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
        _activeRead?.Cancel();
        using var read = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        _activeRead = read;
        int revision = _queryRevision;
        try
        {
            if (!_loaded || !BoardConnection.IsConnected)
            {
                OperationResult<SearchResult> response = await Consumer.Search(_query, _offset, _pageSize, _jobStartAt, _jobEndAt, read.Token, ExcludedStates);
                if (response.StatusCode == (int)HttpStatusCode.Unauthorized) { Session.SetAuthenticated(false); return; }
                response.EnsureSucceeded();
                if (revision != _queryRevision) return;
                _jobs = response.Value?.Items ?? [];
                _totalCount = response.Value?.TotalCount ?? 0;
                _loaded = true;
                _loading = false;
            }
            _error = null;
            await BoardConnection.Configure(revision, _query, _offset, _pageSize, _jobStartAt, _jobEndAt, read.Token, ExcludedStates);
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
        _jobs = snapshot.Items;
        _totalCount = snapshot.TotalCount;
        _loading = false;
        _loaded = true;
        _error = null;
        if (_liveActivity.XValues.Count == 0 && UseLiveChart && !_activityPaused) ApplyLiveActivity();
        StateHasChanged();
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
    private bool IsActivitySeriesHidden(string name) => SeriesStates(name).Any(_hiddenActivitySeries.Contains);
    private string ExcludedStates => string.Join(',', _hiddenActivitySeries);
    private string ActivitySeriesColor(ChartSeries series) => series.Color ?? JobStatusColors.Accent(SeriesStates(series.Name)[0]);
    private IReadOnlyList<ChartSeries> VisibleActivitySeries => ActivityLegendSeries
        .Where(series => !IsActivitySeriesHidden(series.Name))
        .Select(series => new ChartSeries(series.Name, series.Values) { Color = ActivitySeriesColor(series) }).ToArray();

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
        _activeRead?.Cancel();
        await Reload();
    }

    private static readonly string[] ActivityColors = [JobStatusColors.Accent("Scheduled"), JobStatusColors.Accent("Succeeded"), JobStatusColors.Accent("DeadLettered")];
    private long _activityVersion;
    private int _historyRevision;
    private Chart? _activityChart;
    private bool _activityPaused;
    private RealtimeChartData _liveActivity => BoardConnection.LiveActivity.Data;
    private Task? _liveClock;
    private double[] _activityTotals = [0, 0, 0];
    private string[] _activityLabels = [];
    private double[] _activityXValues = [];
    private ChartSeries[] _activitySeries = [];
    private static readonly ChartOptions LiveActivityOptions = new()
    {
        Legend = ChartLegendPosition.None, Width = 1200, Height = 140, ShowYAxis = false, ShowGrid = false, PaddingLeft = 24,
        ShowPoints = false, Animate = false, EnableRangeSelection = true, PauseOnRangeSelection = true,
        EnableRealtimeScrolling = true, RealtimeScrollDuration = TimeSpan.FromSeconds(1),
        Curve = ChartCurve.Monotone, Minimum = 0,
        MaximumXAxisLabels = 7, LabelFormatter = label => label.Length > 8 ? label[..8] : label,
        Palette = [JobStatusColors.Accent("Scheduled"), JobStatusColors.Accent("Running"),
            JobStatusColors.Accent("Succeeded"), JobStatusColors.Accent("DeadLettered"), JobStatusColors.Accent("Queued")]
    };
    private static readonly ChartOptions SearchActivityOptions = new() { Legend = ChartLegendPosition.None, Width = 1200, Height = 140, ShowYAxis = false, ShowGrid = false, ShowPoints = true, Animate = false, EnableRangeSelection = true, Curve = ChartCurve.Monotone, Minimum = 0 };
    private static readonly ChartOptions HistoryActivityOptions = CreateActivityOptions();
    private static ChartOptions CreateActivityOptions() => new()
    {
        Legend = ChartLegendPosition.None, Width = 1200, Height = 140, ShowYAxis = false, ShowGrid = false, PaddingLeft = 24,
        ShowPoints = false, Animate = false, EnableRangeSelection = true,
        PauseOnRangeSelection = true, Curve = ChartCurve.Monotone, Minimum = 0
    };
    private DateTimeOffset? _jobStartAt;
    private DateTimeOffset? _jobEndAt;
    private string? _historyError;
    private bool _historyLoading;
    private bool _liveMode = true;
    private bool UseLiveChart => _liveMode && string.IsNullOrWhiteSpace(_query);
    private string DateRangeLabel => _jobStartAt is { } start && _jobEndAt is { } end
        ? $"{start:MMM d HH:mm}–{end:MMM d HH:mm} UTC"
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
                    StateHasChanged();
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ReloadHistory(CancellationToken cancellationToken)
    {
        int revision = ++_historyRevision;
        if (!string.IsNullOrWhiteSpace(_query))
        {
            try
            {
                _historyError = null;
                _activitySeries = [];
                _activityLabels = [];
                _activityXValues = [];
                _activityVersion++;
                var response = await Consumer.GetSearchHistory(_query, _jobStartAt, _jobEndAt, cancellationToken);
                response.EnsureSucceeded();
                if (revision == _historyRevision) ApplySearchHistory(response.Value ?? []);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { if (revision == _historyRevision) _historyError = "Unable to load matching job activity. Try the search again."; }
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
            _historyError = "Unable to load job history. Reload the page to retry.";
        }
        finally { _historyLoading = false; }
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
        _activeRead?.Cancel();
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
        _activeRead?.Cancel();
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
        _activeRead?.Cancel();
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
