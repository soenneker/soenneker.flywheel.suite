namespace Soenneker.Flywheel.Dashboard;

/// <summary>Configures the dashboard home route and navigation.</summary>
public sealed class DashboardNavigationOptions
{
    /// <summary>Home page for dashboard links and sign-in redirects. Defaults to /flywheel; FlywheelRouter also serves / when configured. The configured home is owned by Flywheel; other host pages can be supplied explicitly to FlywheelRouter.</summary>
    public string HomePath { get; set; } = "/flywheel";
}
