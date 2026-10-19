namespace Soenneker.Flywheel.Dashboard;

internal sealed class DashboardSessionState
{
    public bool IsAuthenticated { get; private set; }
    public bool HasTimedOut { get; private set; }
    public string? Username { get; private set; }
    public event Action? Changed;

    public void Expire()
    {
        if (!IsAuthenticated) return;
        HasTimedOut = true;
        SetAuthenticated(false);
    }

    public void SetAuthenticated(bool authenticated, string? username = null)
    {
        if (authenticated) HasTimedOut = false;
        string? nextUsername = authenticated ? username ?? Username : null;
        if (IsAuthenticated == authenticated && Username == nextUsername) return;
        IsAuthenticated = authenticated;
        Username = nextUsername;
        Changed?.Invoke();
    }
}
