using Soenneker.DataTables.Dtos.ServerSideRequest;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Dashboard.Communication;
using Soenneker.Quark;

namespace Soenneker.Flywheel.Dashboard.Pages.Manual;

public partial class ManualJobs
{
    private List<ManualJobView>? _jobs;
    private string? _error;
    private string? _loadError;
    private string _search = "";
    private int _offset;
    private int _pageSize = 50;
    private Task? _refreshLoop;
    private readonly HashSet<string> _starting = [];
    private readonly DataTableOptions _options = new() { DefaultPageSize = 50, SearchDebounceMs = 300 };
    private IReadOnlyList<ManualJobView> Filtered => _jobs?.Where(j => $"{j.Name} {j.Id} {j.Description}".Contains(_search, StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
    private int Offset => Math.Min(_offset, Math.Max(0, (Filtered.Count - 1) / _pageSize * _pageSize));

    private void Search(DataTableServerSideRequest request)
    {
        _search = request.Search?.Value?.Trim() ?? "";
        _offset = Math.Max(0, request.Start);
        _pageSize = Math.Max(1, request.Length);
    }

    protected override async Task OnInitializedAsync()
    {
        await Refresh();
        _refreshLoop = RefreshLoop();
    }

    private async Task Refresh()
    {
        try
        {
            var response = await Consumer.GetManualJobs(CancellationToken);
            if (response.StatusCode is 401 or 403)
            {
                Session.SetAuthenticated(false);
                _jobs = null;
                _loadError = "Your session has expired. Sign in again.";
                return;
            }
            response.EnsureSucceeded();
            _jobs = response.Value ?? [];
            _loadError = null;
            Session.SetAuthenticated(true);
        }
        catch (Exception) when (!IsDisposed && !IsCancellationRequested)
        {
            _loadError = "Could not load manual jobs. Flywheel will retry automatically.";
        }
    }

    private async Task Run(string id)
    {
        if (!_starting.Add(id)) return;
        _error = null;
        try
        {
            var response = await Consumer.RunManual(id, CancellationToken);
            if (response.StatusCode == 404) { _error = "This manual job no longer exists."; return; }
            response.EnsureSucceeded();
            if (string.IsNullOrEmpty(response.Value?.Id)) throw new InvalidOperationException("Missing execution ID.");
            await Sonner.Success("Job queued", cancellationToken: CancellationToken);
            await Refresh();
        }
        catch (Exception) when (!IsDisposed && !IsCancellationRequested)
        {
            _error = "The run could not be confirmed. Check recent job activity before trying again.";
        }
        finally { _starting.Remove(id); }
    }

    private async Task RefreshLoop()
    {
        CancellationToken token = CancellationToken;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                await Refresh();
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_refreshLoop is not null) await _refreshLoop;
    }
}
