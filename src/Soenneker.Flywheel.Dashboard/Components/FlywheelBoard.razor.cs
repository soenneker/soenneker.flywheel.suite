using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Soenneker.Quark;
using Soenneker.DataTables.Dtos.ServerSideRequest;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Dashboard.Communication;
using Soenneker.Flywheel.Dashboard.Communication.Abstract;
using System.Net;

namespace Soenneker.Flywheel.Dashboard;

public partial class FlywheelBoard
{
private async Task OpenJobDrawer(KeyboardEventArgs e, JobView job)
    {
        if (e.Key is "Enter" or " ") await ShowJobDrawer(job);
    }

    private Drawer? _jobDrawer;
    private async Task ShowJobDrawer(JobView job)
    {
        _selectedSchedule = null;
        _selectedJob = job;
        await InvokeAsync(StateHasChanged);
        if (_jobDrawer is not null) await _jobDrawer.Show();
    }

    private async Task ShowScheduleDrawer(RecurringScheduleView schedule)
    {
        _selectedJob = null;
        _selectedSchedule = schedule;
        await InvokeAsync(StateHasChanged);
        if (_jobDrawer is not null) await _jobDrawer.Show();
    }

    protected override void OnInitialized()
    {
        ActivityTotals.Changed += TotalsChanged;
    }

    private void TotalsChanged() => _ = InvokeAsync(StateHasChanged);

    private readonly DataTableOptions _recurringTableOptions = new() { DefaultPageSize = 10, SearchDebounceMs = 300 };
    private int _recurringOffset;
    private int _recurringPageSize = 10;
    private int RecurringOffset => Math.Min(_recurringOffset, Math.Max(0, (FilteredRecurring.Count - 1) / _recurringPageSize * _recurringPageSize));
    private void SearchRecurring(DataTableServerSideRequest request)
    {
        _recurringSearch = request.Search?.Value?.Trim() ?? "";
        _recurringOffset = Math.Max(0, request.Start);
        _recurringPageSize = Math.Max(1, request.Length);
    }

    private readonly DataTableOptions _scheduledTableOptions = new() { DefaultPageSize = 10, SearchDebounceMs = 300 };
    private int _scheduledOffset;
    private int _scheduledPageSize = 10;
    private int ScheduledOffset => Math.Min(_scheduledOffset, Math.Max(0, (FilteredScheduled.Count - 1) / _scheduledPageSize * _scheduledPageSize));
    private void SearchScheduled(DataTableServerSideRequest request)
    {
        _scheduledSearch = request.Search?.Value?.Trim() ?? "";
        _scheduledOffset = Math.Max(0, request.Start);
        _scheduledPageSize = Math.Max(1, request.Length);
    }

    private string _recurringSearch = "";
    private string _scheduledSearch = "";
    private List<RecurringScheduleView>? _recurringSource;
    private List<JobView>? _scheduledSource;
    private string? _cachedRecurringSearch, _cachedScheduledSearch;
    private IReadOnlyList<RecurringScheduleView> _filteredRecurring = [];
    private IReadOnlyList<JobView> _filteredScheduled = [];
    private IReadOnlyList<RecurringScheduleView> FilteredRecurring
    {
        get
        {
            var source = _schedules?.Recurring;
            if (ReferenceEquals(source, _recurringSource) && _cachedRecurringSearch == _recurringSearch)
                return _filteredRecurring;
            _recurringSource = source;
            _cachedRecurringSearch = _recurringSearch;
            return _filteredRecurring = source is null ? [] : _recurringSearch.Length == 0 ? source :
                source.Where(schedule => $"{schedule.Name} {schedule.Id} {schedule.Cron} {schedule.TimeZoneId} Every {IntervalLabel(schedule.Interval)}"
                    .Contains(_recurringSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }
    private IReadOnlyList<JobView> FilteredScheduled
    {
        get
        {
            var source = _schedules?.Scheduled;
            if (ReferenceEquals(source, _scheduledSource) && _cachedScheduledSearch == _scheduledSearch)
                return _filteredScheduled;
            _scheduledSource = source;
            _cachedScheduledSearch = _scheduledSearch;
            return _filteredScheduled = source is null ? [] : _scheduledSearch.Length == 0 ? source :
                source.Where(job => $"{job.Name} {job.Id}".Contains(_scheduledSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }

    /// <summary>Renders the sign-in page and returns authenticated users to the configured home page.</summary>
    [Parameter]
    public bool SignInOnly { get; set; }

    private bool IsSignInPage => Navigation.ToAbsoluteUri(Navigation.Uri).AbsolutePath.TrimEnd('/') == "/signin";

    /// <summary>Dashboard section to display: overview, recurring, or scheduled.</summary>
    [Parameter]
    public string View { get; set; } = "overview";

    /// <summary>Indicates that the containing layout already provides the application header.</summary>
    [CascadingParameter(Name = "FlywheelSharedHeader")]
    public bool SharedHeader { get; set; }

    /// <summary>Invoked after a successful sign-in so a containing page can resume its request.</summary>
    [Parameter]
    public EventCallback SignedIn { get; set; }
    private void RefreshPage() => Navigation.NavigateTo(Navigation.Uri, forceLoad: true);
    private static readonly string[] ActivityColors = [JobStatusColors.Foreground("Scheduled"), JobStatusColors.Foreground("Succeeded"), JobStatusColors.Foreground("DeadLettered")];
    private long _activityVersion;
    private int _activityChartKey;
    private double[] _activityTotals = [0, 0, 0];
    private string[] _activityLabels = [];
    private double[] _activityXValues = [];
    private ChartSeries[] _activitySeries = [];
    private readonly ChartOptions _activityOptions = new() { Width = 1200, Height = 200, ShowYAxis = false, ShowGrid = false, PaddingLeft = 24, ShowPoints = false, Animate = false, EnableRangeSelection = true };
    private DateTimeOffset? _jobStartAt;
    private DateTimeOffset? _jobEndAt;
    private string? _historyError;
    private bool _historyLoading;
    private bool _lastHour = true;
    private int _historyRetentionDays = 1;
    private DateOnly _historyStartDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateOnly _historyEndDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateOnly HistoryMaxDate => DateOnly.FromDateTime(DateTime.UtcNow);
    private DateOnly HistoryMinDate => HistoryMaxDate.AddDays(-_historyRetentionDays + 1);
    private CalendarDateRange SelectedHistoryRange => new(_historyStartDate, _historyEndDate);
    private IReadOnlyList<PresetDateRangePickerOption> HistoryRangePresets =>
        new[] { 1, 3, 7, 14, 30 }
            .Where(days => days <= _historyRetentionDays)
            .Select(days => PresetDateRangePickerOption.LastDays(days, days == 1 ? "Today" : null))
            .ToArray();

    private async Task LoadHistoryOptions(CancellationToken cancellationToken)
    {
        var response = await Consumer.GetHistoryOptions(cancellationToken);
        if (response.StatusCode == (int)HttpStatusCode.NotImplemented) return;
        response.EnsureSucceeded();
        var options = response.Value
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
        if (!_lastHour && _historyStartDate == from && _historyEndDate == to) return;
        _lastHour = false;
        ActivityTotals.LastHour = false;
        _historyStartDate = from;
        _historyEndDate = to;
        await ReloadHistory(CancellationToken);
    }

    private async Task ShowLastHour()
    {
        _lastHour = true;
        ActivityTotals.LastHour = true;
        await ReloadHistory(CancellationToken);
    }

    private static List<JobHistoryPoint> LastHourPoints(List<JobHistoryPoint> points)
    {
        long firstBucket = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds() / 300000 * 300000;
        return points.Where(point => point.Timestamp >= firstBucket).ToList();
    }

    private async Task ReloadHistory(CancellationToken cancellationToken)
    {
        try
        {
            _historyLoading = true;
            DateTimeOffset startAt = new(_historyStartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            DateTimeOffset requestedEnd = new(_historyEndDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            DateTimeOffset endAt = requestedEnd > DateTimeOffset.UtcNow ? DateTimeOffset.UtcNow : requestedEnd;
            if (_lastHour) { endAt = DateTimeOffset.UtcNow; startAt = endAt.AddHours(-1); }
            var response = await Consumer.GetHistory(startAt, endAt, cancellationToken);
            response.EnsureSucceeded();
            var points = response.Value
                ?? throw new InvalidOperationException("Missing history response.");
            _activityXValues = points.Select(point => (double)point.Timestamp).ToArray();
            _activityLabels = points.Select(point => DateTimeOffset.FromUnixTimeMilliseconds(point.Timestamp).ToString("MMM d HH:mm")).ToArray();
            double[][] values = [points.Select(p => (double)p.Scheduled).ToArray(),
                points.Select(p => (double)p.Succeeded).ToArray(), points.Select(p => (double)p.DeadLettered).ToArray()];
            string[] labels = ["Scheduled", "Succeeded", "Dead lettered"];
            _activityTotals = values.Select(series => series.Sum()).ToArray();
            ActivityTotals.Update(_activityTotals);
            _activityVersion++;
            _activitySeries = labels.Select((label, index) => new ChartSeries(label, values[index]) { Color = ActivityColors[index] }).ToArray();
            _historyError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _historyError = "Unable to load job history. Reload the page to retry.";
        }
        finally { _historyLoading = false; }
    }

    private Task SelectActivityRange(ChartRangeSelection selection) =>
        SetActivitySelection(selection.StartIndex, selection.EndIndex);

    private async Task SetActivitySelection(int startIndex, int endIndex)
    {
        if ((uint)startIndex >= (uint)_activityXValues.Length || (uint)endIndex >= (uint)_activityXValues.Length) return;
        _jobStartAt = DateTimeOffset.FromUnixTimeMilliseconds((long)_activityXValues[startIndex]);
        _jobEndAt = DateTimeOffset.FromUnixTimeMilliseconds((long)_activityXValues[endIndex]).AddMinutes(5);
        _offset = 0;
        _queryRevision++;
        _activeRead?.Cancel();
        await Reload(true);
    }

    private async Task ClearActivitySelection()
    {
        _jobStartAt = null;
        _jobEndAt = null;
        _activityChartKey++;
        _offset = 0;
        _queryRevision++;
        _activeRead?.Cancel();
        await Reload(true);
    }

    private readonly SemaphoreSlim _reloadGate = new(1);
    private List<JobView> _jobs = [];
    private readonly string _signalId = $"flywheel-board-{Guid.NewGuid():N}";
    private IFlywheelLiveSubscription? _connection;
    private Task? _connecting;
    private string _username = "admin", _password = "";
    private string? _error;
    private bool _authChecked, _login, _busy;
    private int _offset, _totalCount, _queryRevision;
    private int _pageSize = 50;
    private string _query = "";
    private RecurringScheduleView? _selectedSchedule;
    private async Task OpenScheduleDrawer(KeyboardEventArgs e, RecurringScheduleView schedule)
    {
        if (e.Key is "Enter" or " ") await ShowScheduleDrawer(schedule);
    }
    private JobView? _selectedJob;

    private void OnJobDrawerVisibleChanged(bool visible)
    {
        if (!visible)
        {
            _selectedJob = null;
            _selectedSchedule = null;
        }
    }
    private CancellationTokenSource? _activeRead;
    private readonly DataTableOptions _tableOptions = new() { DefaultPageSize = 50, SearchDebounceMs = 300 };
    private DateTimeOffset? _lastRefresh;
    private ScheduleView? _schedules;
    private string? _scheduleError;

    private static string IntervalLabel(long milliseconds)
    {
        var interval = TimeSpan.FromMilliseconds(milliseconds);
        if (milliseconds % 86400000 == 0) return $"{interval.TotalDays:0}d";
        if (milliseconds % 3600000 == 0) return $"{interval.TotalHours:0}h";
        if (milliseconds % 60000 == 0) return $"{interval.TotalMinutes:0}m";
        return $"{interval.TotalSeconds:0.###}s";
    }

    private async Task ReloadSchedules(CancellationToken cancellationToken)
    {
        try
        {
            var response = await Consumer.GetSchedules(cancellationToken);
            if (response.StatusCode == (int)HttpStatusCode.NotImplemented)
            {
                _scheduleError = "Schedule information is unavailable from this job store.";
                return;
            }
            response.EnsureSucceeded();
            _schedules = response.Value;
            ActivityTotals.UpdateScheduled(_schedules?.Scheduled.Count);
            _scheduleError = _schedules is null ? "Schedule information is unavailable." : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _scheduleError = "Unable to load schedules. Reload the page to retry.";
        }
    }

    protected override Task OnInitializedAsync() => Reload();

    private Task Reload() => Reload(false);

    private async Task Reload(bool wait)
    {
        if (_query.Length > 200)
            return;
        if (_connection?.IsConnected == true)
        {
            try { await SubscribeLive(); }
            catch (OperationCanceledException) when (IsDisposed || IsCancellationRequested) { }
            catch (Exception) { _error = "Could not refresh live results. Reload the page to reconnect."; }
            return;
        }
        if (wait)
            await _reloadGate.WaitAsync(CancellationToken);
        else if (!await _reloadGate.WaitAsync(0))
            return;
        using var read = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        _activeRead = read;
        var revision = _queryRevision;
        try
        {
            var response = await Consumer.Search(_query, _offset, _pageSize, _jobStartAt, _jobEndAt, read.Token);
            if (response.StatusCode == (int)HttpStatusCode.Unauthorized)
            {
                Session.SetAuthenticated(false);
                _authChecked = true;
                _login = true;
                _jobs.Clear();
                _activityLabels = []; _activityXValues = []; _activitySeries = [];
                _schedules = null;
                ActivityTotals.UpdateLive(false);
                if (DashboardNavigation.HomePath == "/" && !SignInOnly && !IsSignInPage)
                    Navigation.NavigateTo("/signin", replace: true);
                return;
            }

            response.EnsureSucceeded();
            if (SignInOnly)
            {
                Navigation.NavigateTo(DashboardNavigation.HomePath, replace: true);
                return;
            }
            var result = response.Value;
            if (revision != _queryRevision)
                return;
            _jobs = result?.Items ?? [];
            _totalCount = result?.TotalCount ?? 0;
            _authChecked = true;
            _login = false;
            Session.SetAuthenticated(true);
            _error = null;
            _lastRefresh = DateTimeOffset.UtcNow;
            await LoadHistoryOptions(read.Token);
            await ReloadHistory(read.Token);
            await ReloadSchedules(read.Token);
            _connecting ??= Connect();
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (revision == _queryRevision)
            {
                _authChecked = true;
                _error = "Cannot reach the dashboard API. Reload the page to reconnect.";
                ActivityTotals.Clear();
            }
        }
        finally
        {
            _activeRead = null;
            _reloadGate.Release();
            if (!IsDisposed && !IsCancellationRequested)
                await InvokeAsync(StateHasChanged);
        }
    }

    private Task SubscribeLive() => _connection is null ? Task.CompletedTask :
        _connection.SubscribeBoard(_queryRevision, _query, _offset, _pageSize, _jobStartAt, _jobEndAt, CancellationToken);

    private void ApplyLive(LiveBoard snapshot)
    {
        if (IsDisposed || IsCancellationRequested || snapshot.Version != _queryRevision || _login) return;
        ActivityTotals.UpdateRunning(snapshot.RunningCount);
        ActivityTotals.UpdateScheduled(snapshot.Schedules?.Scheduled.Count);
        ActivityTotals.UpdateServers(snapshot.ServerCount, snapshot.TotalWorkers);
        _jobs = snapshot.Items;
        _totalCount = snapshot.TotalCount;
        _lastRefresh = DateTimeOffset.UtcNow;
        _error = null;
            _schedules = snapshot.Schedules;
            if (_selectedSchedule is { } currentSchedule && _schedules is not null)
                _selectedSchedule = _schedules.Recurring.FirstOrDefault(schedule => schedule.Id == currentSchedule.Id) ?? currentSchedule;
            _scheduleError = snapshot.Schedules is null ? "Schedule information is unavailable from this job store." : null;
            if (snapshot.History is { } points && _lastHour)
            {
                points = LastHourPoints(points);
                _activityXValues = points.Select(point => (double)point.Timestamp).ToArray();
                _activityLabels = points.Select(point => DateTimeOffset.FromUnixTimeMilliseconds(point.Timestamp).ToString("MMM d HH:mm")).ToArray();
                double[][] values = [points.Select(p => (double)p.Scheduled).ToArray(),
                    points.Select(p => (double)p.Succeeded).ToArray(), points.Select(p => (double)p.DeadLettered).ToArray()];
                string[] labels = ["Scheduled", "Succeeded", "Dead lettered"];
                _activityTotals = values.Select(series => series.Sum()).ToArray();
            ActivityTotals.Update(_activityTotals);
                _activityVersion++;
            _activitySeries = labels.Select((label, index) => new ChartSeries(label, values[index]) { Color = ActivityColors[index] }).ToArray();
                _historyError = null;
            }
            else if (snapshot.History is null) _historyError = "History is unavailable from this job store.";
        StateHasChanged();
    }

    private async Task Connect()
    {
        try
        {
            _connection = await Live.Board(_signalId,
                snapshot => InvokeAsync(() => ApplyLive(snapshot)),
                () => InvokeAsync(async () => { await SubscribeLive(); ActivityTotals.UpdateLive(true); StateHasChanged(); }),
                () => InvokeAsync(() => { ActivityTotals.UpdateLive(false); StateHasChanged(); }), CancellationToken);
            await _connection.Start(CancellationToken);
            ActivityTotals.UpdateLive(_connection.IsConnected);
            if (!IsDisposed && !IsCancellationRequested && !_connection.IsConnected)
            {
                _error = "Live updates are unavailable. Reload the page to reconnect.";
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) when (IsDisposed || IsCancellationRequested) { }
        catch (Exception)
        {
            ActivityTotals.Clear();
            _error = "Live updates are unavailable. Reload the page to reconnect.";
            if (!IsDisposed && !IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task Login()
    {
        _busy = true;
        try
        {
            var response = await Consumer.Login(_username, _password, CancellationToken);
            if (!response.Succeeded)
            {
                _error = response.StatusCode == (int)HttpStatusCode.TooManyRequests ? "Too many attempts. Try again in a minute." : "Sign-in failed. Check your credentials.";
                return;
            }

            if (SignInOnly)
            {
                Navigation.NavigateTo(DashboardNavigation.HomePath, forceLoad: true, replace: true);
                return;
            }
            if (SharedHeader) { RefreshPage(); return; }
            await Reload();
            await SignedIn.InvokeAsync();
        }
        catch (Exception)
        {
            _error = "Sign-in unavailable. Check the server connection.";
        }
        finally
        {
            _password = "";
            _busy = false;
        }
    }

    private readonly HashSet<string> _startingSchedules = [];

    private async Task RunRecurring(string scheduleId)
    {
        if (!_startingSchedules.Add(scheduleId)) return;
        try
        {
            var response = await Consumer.RunRecurring(scheduleId, CancellationToken);
            if (response.StatusCode == (int)HttpStatusCode.NotFound)
            {
                _error = "This recurring schedule no longer exists.";
                return;
            }
            response.EnsureSucceeded();
            var job = response.Value;
            if (job is null || string.IsNullOrEmpty(job.Id)) throw new InvalidOperationException("Missing execution ID.");
            Navigation.NavigateTo($"jobs/{Uri.EscapeDataString(job.Id)}");
        }
        catch (Exception)
        {
            _error = "The run could not be confirmed. Check recent job activity before trying again.";
        }
        finally
        {
            _startingSchedules.Remove(scheduleId);
        }
    }

    private readonly HashSet<string> _cancellingJobs = [];

    private async Task Cancel(string id)
    {
        if (!_cancellingJobs.Add(id)) return;
        try
        {
            var response = await Consumer.CancelJob(id, CancellationToken);
            if (response.StatusCode == (int)HttpStatusCode.Unauthorized) { RefreshPage(); return; }
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

    private void SearchTextChanged(string query)
    {
        _query = query;
        _offset = 0;
        _queryRevision++;
        _activeRead?.Cancel();
    }

    private async Task SearchJobs(DataTableServerSideRequest request)
    {
        _query = request.Search?.Value ?? "";
        _offset = request.Start;
        _pageSize = request.Length;
        _queryRevision++;
        _activeRead?.Cancel();
        if (_query.Length > 200)
        {
            _jobs.Clear();
            _totalCount = 0;
            _error = "Search must be 200 characters or fewer.";
            return;
        }

        await Reload(true);
    }

    public override async ValueTask DisposeAsync()
    {
        ActivityTotals.Changed -= TotalsChanged;
        ActivityTotals.Clear();
        await base.DisposeAsync();
        if (_connecting is not null)
            await _connecting;
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
