namespace Soenneker.Flywheel.Dashboard;

/// <summary>Configures the dashboard home route and navigation.</summary>
public sealed class DashboardNavigationOptions
{
    /// <summary>Home page and prefix for dashboard navigation and sign-in redirects. Defaults to /flywheel; FlywheelRouter also serves / when configured. The configured home is owned by Flywheel; other host pages can be supplied explicitly to FlywheelRouter.</summary>
    public string HomePath { get; set; } = "/flywheel";
    /// <summary>Returns an application-base-relative dashboard link under the configured home path.</summary>
    public string Path(string page = "") => string.Join("/", new[] { HomePath.Trim('/'), page.Trim('/') }.Where(segment => segment.Length > 0));
}
