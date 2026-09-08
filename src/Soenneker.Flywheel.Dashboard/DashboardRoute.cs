namespace Soenneker.Flywheel.Dashboard;

internal readonly record struct DashboardRoute(DashboardPage Page, string? Id = null)
{
    public static DashboardRoute Match(string baseRelativePath, string homePath)
    {
        var path = baseRelativePath.Split('?', '#')[0].TrimEnd('/');
        if (path.Equals("flywheel", StringComparison.OrdinalIgnoreCase) || (path.Length == 0 && homePath == "/"))
            return new(DashboardPage.Dashboard);
        if (path.Equals("signin", StringComparison.OrdinalIgnoreCase))
            return new(DashboardPage.SignIn);
        if (path.Equals("flywheel/recurring", StringComparison.OrdinalIgnoreCase))
            return new(DashboardPage.Recurring);
        if (path.Equals("flywheel/scheduled", StringComparison.OrdinalIgnoreCase))
            return new(DashboardPage.Scheduled);
        if (path.Equals("flywheel/servers", StringComparison.OrdinalIgnoreCase))
            return new(DashboardPage.Servers);
        if (TryReadId(path, "jobs/", out var jobId))
            return new(DashboardPage.Jobs, jobId);
        if (TryReadId(path, "flywheel/servers/", out var serverId))
            return new(DashboardPage.ServerDetails, serverId);
        return new(DashboardPage.NotFound);
    }

    private static bool TryReadId(string path, string prefix, out string? id)
    {
        id = null;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var segment = path[prefix.Length..];
        if (segment.Length == 0 || segment.Contains('/'))
            return false;
        id = Uri.UnescapeDataString(segment);
        return true;
    }
}
