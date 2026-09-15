namespace Soenneker.Flywheel.Dashboard;

internal sealed class DashboardSessionState
{
    public bool IsAuthenticated { get; private set; }
    public string? Username { get; private set; }
    public event Action? Changed;

    public void SetAuthenticated(bool authenticated, string? username = null)
    {
        string? nextUsername = authenticated ? username ?? Username : null;
        if (IsAuthenticated == authenticated && Username == nextUsername) return;
        IsAuthenticated = authenticated;
        Username = nextUsername;
        Changed?.Invoke();
    }
}
