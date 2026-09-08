using Microsoft.AspNetCore.Components.Web;
using Soenneker.Quark;
using static Soenneker.Flywheel.Dashboard.Pages.Schedules.Shared.ScheduleFormatting;
using Soenneker.DataTables.Dtos.ServerSideRequest;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Dashboard.Communication;
using Soenneker.Dtos.Results.Operation;
using System.Net;

namespace Soenneker.Flywheel.Dashboard.Pages.Schedules;

public partial class Schedules
{
    private string? _error;
    private string _recurringSearch = "";
    private List<RecurringScheduleView>? _recurringSource;
    private string? _cachedRecurringSearch;
    private IReadOnlyList<RecurringScheduleView> _filteredRecurring = [];
    private RecurringScheduleView? _selectedSchedule;
    private Drawer? _drawer;

    private async Task ShowScheduleDrawer(RecurringScheduleView schedule)
    {
        _selectedSchedule = schedule;
        await InvokeAsync(StateHasChanged);
        if (_drawer is not null) await _drawer.Show();
    }
    private Task OpenScheduleDrawer(KeyboardEventArgs e, RecurringScheduleView schedule) =>
        e.Key is "Enter" or " " ? ShowScheduleDrawer(schedule) : Task.CompletedTask;
    private void OnDrawerVisibleChanged(bool visible) { if (!visible) _selectedSchedule = null; }
    protected override void SchedulesChanged()
    {
        if (_selectedSchedule is { } selected && _schedules is not null)
            _selectedSchedule = _schedules.Recurring.FirstOrDefault(schedule => schedule.Id == selected.Id) ?? selected;
    }
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

    private IReadOnlyList<RecurringScheduleView> FilteredRecurring
    {
        get
        {
            List<RecurringScheduleView>? source = _schedules?.Recurring;
            if (ReferenceEquals(source, _recurringSource) && _cachedRecurringSearch == _recurringSearch)
                return _filteredRecurring;
            _recurringSource = source;
            _cachedRecurringSearch = _recurringSearch;
            return _filteredRecurring = source is null ? [] : _recurringSearch.Length == 0 ? source :
                source.Where(schedule => $"{schedule.Name} {schedule.Id} {schedule.Cron} {schedule.TimeZoneId} Every {IntervalLabel(schedule.Interval)}"
                    .Contains(_recurringSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }
    private readonly HashSet<string> _startingSchedules = [];

    private async Task RunRecurring(string scheduleId)
    {
        if (!_startingSchedules.Add(scheduleId)) return;
        _error = null;
        try
        {
            OperationResult<StartedJob> response = await Consumer.RunRecurring(scheduleId, CancellationToken);
            if (response.StatusCode == (int)HttpStatusCode.NotFound)
            {
                _error = "This recurring schedule no longer exists.";
                return;
            }
            response.EnsureSucceeded();
            StartedJob? job = response.Value;
            if (job is null || string.IsNullOrEmpty(job.Id)) throw new InvalidOperationException("Missing execution ID.");
            await Sonner.Success("Job queued", cancellationToken: CancellationToken);
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

}
