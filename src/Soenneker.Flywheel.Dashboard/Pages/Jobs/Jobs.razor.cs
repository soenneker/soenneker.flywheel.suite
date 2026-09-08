using Microsoft.AspNetCore.Components.Web;
using Soenneker.Quark;
using Soenneker.DataTables.Dtos.ServerSideRequest;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Pages.Jobs;

public partial class Jobs
{
    private string _scheduledSearch = "";
    private List<JobView>? _scheduledSource;
    private string? _cachedScheduledSearch;
    private IReadOnlyList<JobView> _filteredScheduled = [];
    private JobDetailsDrawer? _jobDrawer;
    private Task ShowJobDrawer(JobView job) => _jobDrawer?.Show(job) ?? Task.CompletedTask;
    private Task OpenJobDrawer(KeyboardEventArgs e, JobView job) => e.Key is "Enter" or " " ? ShowJobDrawer(job) : Task.CompletedTask;
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

    private IReadOnlyList<JobView> FilteredScheduled
    {
        get
        {
            List<JobView>? source = _schedules?.Scheduled;
            if (ReferenceEquals(source, _scheduledSource) && _cachedScheduledSearch == _scheduledSearch)
                return _filteredScheduled;
            _scheduledSource = source;
            _cachedScheduledSearch = _scheduledSearch;
            return _filteredScheduled = source is null ? [] : _scheduledSearch.Length == 0 ? source :
                source.Where(job => $"{job.Name} {job.Id}".Contains(_scheduledSearch, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }

}
