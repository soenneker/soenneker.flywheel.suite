using Soenneker.Flywheel.Dashboard.Communication;
using Soenneker.Flywheel.Dashboard.Communication.Abstract;
using Soenneker.Flywheel.Dashboard.Consumers.Abstract;
using System.Net;
using Microsoft.AspNetCore.Components;
using Soenneker.Dtos.Results.Operation;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Pages.Jobs;

public partial class Job
{
    [Inject]
    private DashboardSessionState Session { get; set; } = null!;

    /// <summary>Identifier of the execution selected by the route.</summary>
    [Parameter]
    public string JobId { get; set; } = "";

    /// <summary>Typed consumer for dashboard execution operations.</summary>
    [Inject]
    public IFlywheelDashboardConsumer Consumer { get; set; } = null!;

    /// <summary>Controls page navigation within the hosting application.</summary>
    [Inject]
    public NavigationManager Navigation { get; set; } = null!;

    /// <summary>Creates the page's live execution subscription.</summary>
    [Inject]
    public IFlywheelLiveClient Live { get; set; } = null!;

    /// <summary>Displays details inside a drawer without changing the page title or showing back navigation.</summary>
    [Parameter]
    public bool Embedded { get; set; }

    /// <summary>The previous page URL, including its query string and fragment; defaults to the dashboard home for direct visits.</summary>
    [Parameter]
    public string? BackHref { get; set; }

    private readonly SemaphoreSlim _readGate = new(1);
    private readonly string _signalId = $"flywheel-job-{Guid.NewGuid():N}";
    private JobView? _job;
    private IFlywheelLiveSubscription? _connection;
    private int _version;
    private Task? _connecting;
    private bool _loading, _login, _missing, _cancelling;
    private string? _error;
    private void RefreshPage() => Navigation.NavigateTo(Navigation.Uri, forceLoad: true);

    protected override async Task OnParametersSetAsync()
    {
        _job = null;
        _missing = false;
        _loading = true;
        _error = null;
        _version++;
        await Reload();
    }

    private async Task Reload()
    {
        if (IsCancellationRequested) return;
        if (_connection?.IsConnected == true)
        {
            try { await SubscribeLive(); }
            catch (OperationCanceledException) when (IsDisposed || IsCancellationRequested) { }
            catch (Exception) { _loading = false; _error = "Could not refresh this execution. Reload the page to reconnect."; }
            return;
        }
        await _readGate.WaitAsync(CancellationToken);
        string id = JobId;
        _loading = true;
        try
        {
            OperationResult<JobView> response = await Consumer.GetJob(id, CancellationToken);
            if (id != JobId) return;
            _login = response.StatusCode == (int)HttpStatusCode.Unauthorized;
            if (_login) Session.SetAuthenticated(false);
            else if (response.Succeeded) Session.SetAuthenticated(true);
            _missing = response.StatusCode == (int)HttpStatusCode.NotFound;
            if (_login || _missing) { _job = null; _error = null; return; }
            response.EnsureSucceeded();
            JobView? job = response.Value;
            if (id != JobId) return;
            _job = job;
            _error = null;
            _connecting ??= Connect();
        }
        catch (OperationCanceledException) when (IsDisposed || IsCancellationRequested) { }
        catch (Exception) { _error = "Could not load this execution. Reload the page to retry."; }
        finally
        {
            _loading = false;
            _readGate.Release();
            if (!IsDisposed && !IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }

    private Task SubscribeLive() => _connection is null ? Task.CompletedTask :
        _connection.SubscribeJob(_version, JobId, CancellationToken);

    private async Task Connect()
    {
        try
        {
            _connection = await Live.Job(_signalId, snapshot => InvokeAsync(() =>
            {
                if (IsDisposed || IsCancellationRequested || snapshot.Version != _version || snapshot.JobId != JobId) return;
                _job = snapshot.Job; _missing = _job is null; _loading = false; _login = false; _error = null;
                StateHasChanged();
            }), () => InvokeAsync(SubscribeLive),
                () => InvokeAsync(() => { _error = "Live updates disconnected. Reconnecting…"; StateHasChanged(); }), CancellationToken);
            await _connection.Start(CancellationToken);
            if (!IsDisposed && !IsCancellationRequested && !_connection.IsConnected)
            {
                _loading = false;
                _error = "Live updates are unavailable. Reload the page to reconnect.";
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) when (IsDisposed || IsCancellationRequested) { }
        catch (Exception)
        {
            _error = "Live updates are unavailable. Reload the page to reconnect.";
            if (!IsDisposed && !IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task Cancel()
    {
        if (_cancelling) return;
        _cancelling = true;
        try
        {
            OperationResult<object> response = await Consumer.CancelJob(JobId, CancellationToken);
            if (response.StatusCode == (int)HttpStatusCode.Unauthorized) { Session.SetAuthenticated(false); _login = true; _job = null; return; }
            if (response.StatusCode == (int)HttpStatusCode.Conflict)
            {
                await Reload();
                _error = "This execution can no longer be cancelled. Its status may have changed.";
                return;
            }
            response.EnsureSucceeded();
            await Reload();
        }
        catch (OperationCanceledException) when (IsDisposed || IsCancellationRequested) { }
        catch (Exception) { _error = "Cancellation was not confirmed. Refresh and retry if the job is still active."; }
        finally { _cancelling = false; }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_connecting is not null) await _connecting;
        if (_connection is not null) await _connection.DisposeAsync();
        await _readGate.WaitAsync();
        _readGate.Release();
    }
}
