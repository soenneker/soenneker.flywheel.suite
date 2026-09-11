using Soenneker.Dtos.Results.Operation;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Dashboard.Communication;

namespace Soenneker.Flywheel.Dashboard;

public partial class FlywheelLayout
{
    private readonly CancellationTokenSource _stop = new();
    private bool _checking = true, _disposed;
    private string? _error;
    private Task? _connecting;
    private bool IsSignInPage => Navigation.ToBaseRelativePath(Navigation.Uri).Split('?', '#')[0]
        .TrimEnd('/').Equals(DashboardNavigation.Path("signin"), StringComparison.OrdinalIgnoreCase);

    protected override async Task OnInitializedAsync()
    {
        Session.Changed += OnSessionChanged;
        try
        {
            OperationResult<SearchResult> response = await Consumer.Search(count: 1, cancellationToken: _stop.Token);
            if (response.StatusCode != 401) response.EnsureSucceeded();
            if (_disposed) return;
            Session.SetAuthenticated(response.Succeeded);
            _checking = false;
            if (Session.IsAuthenticated) _connecting = StartConnection();
            EnforceAuthentication();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception)
        {
            _checking = false;
            _error = "Cannot reach the dashboard API. Reload the page to retry.";
        }
    }

    protected override void OnParametersSet() => EnforceAuthentication();

    private void EnforceAuthentication()
    {
        if (!_checking && !_disposed && _error is null && !Session.IsAuthenticated && !IsSignInPage)
            Navigation.NavigateTo(DashboardNavigation.Path("signin"), replace: true);
    }

    private async Task StartConnection()
    {
        try { await BoardConnection.EnsureStarted(_stop.Token); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { if (!_disposed) ActivityTotals.UpdateLive(false); }
    }

    private void OnSessionChanged() => _ = InvokeAsync(async () =>
    {
        if (_checking || _disposed) return;
        if (Session.IsAuthenticated) _connecting = StartConnection();
        else await BoardConnection.Stop();
        if (_disposed) return;
        EnforceAuthentication();
        StateHasChanged();
    });

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Session.Changed -= OnSessionChanged;
        await _stop.CancelAsync();
        if (_connecting is not null) await _connecting;
        await BoardConnection.Stop();
        _stop.Dispose();
    }
}
