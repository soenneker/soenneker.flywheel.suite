namespace Soenneker.Flywheel.Dashboard;

/// <summary>Configures independent dashboard navigation and engine endpoint prefixes.</summary>
public sealed class DashboardNavigationOptions
{
    /// <summary>Home page and prefix for dashboard navigation. Defaults to /flywheel; use / for no prefix. Independent of EnginePath.</summary>
    public string HomePath { get; set; } = "/flywheel";
    /// <summary>Prefix for API, authentication, and SignalR requests. Defaults to /flywheel; use / for no prefix. Must match the backend DashboardOptions.EnginePath.</summary>
    public string EnginePath { get; set; } = "/flywheel";

    /// <summary>Returns an engine endpoint relative to the configured backend base address.</summary>
    public string EngineEndpoint(string endpoint) => Soenneker.Flywheel.Communication.DashboardPaths.Relative(EnginePath, endpoint);
    /// <summary>Returns an application-base-relative dashboard link under the configured home path.</summary>
    public string Path(string page = "")
    {
        string path = string.Join("/", new[] { HomePath.Trim('/'), page.Trim('/') }.Where(segment => segment.Length > 0));
        return path.Length == 0 ? "./" : path;
    }
}
