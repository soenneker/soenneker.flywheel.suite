namespace Soenneker.Flywheel.Dashboard;

internal sealed class DashboardSessionState
{
    public bool IsAuthenticated { get; private set; }
    public event Action? Changed;

    public void SetAuthenticated(bool authenticated)
    {
        if (IsAuthenticated == authenticated) return;
        IsAuthenticated = authenticated;
        Changed?.Invoke();
    }
}
