using System.Net;
using Soenneker.Dtos.Results.Operation;

namespace Soenneker.Flywheel.Dashboard.Pages.Authentication;

public partial class SignIn
{
    private string _username = "admin", _password = "";
    private string? _error;
    private bool _busy;

    protected override void OnInitialized()
    {
        if (Session.IsAuthenticated) Navigation.NavigateTo(DashboardNavigation.HomePath.TrimStart('/'), replace: true);
    }

    private async Task Login()
    {
        if (_busy) return;
        _busy = true;
        _error = null;
        try
        {
            OperationResult<object> response = await Consumer.Login(_username, _password, CancellationToken);
            if (!response.Succeeded)
            {
                _error = response.StatusCode == (int)HttpStatusCode.TooManyRequests ? "Too many attempts. Try again in a minute." : "Sign-in failed. Check your credentials.";
                return;
            }
            Session.SetAuthenticated(true);
            Navigation.NavigateTo(DashboardNavigation.HomePath.TrimStart('/'), replace: true);
        }
        catch (OperationCanceledException) when (IsCancellationRequested) { }
        catch (Exception) { _error = "Sign-in unavailable. Check the server connection."; }
        finally { _password = ""; _busy = false; }
    }
}
