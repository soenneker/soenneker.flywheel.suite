using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Dashboard;

internal readonly record struct DashboardRoute(DashboardPage Page, string? Id = null)
{
    public static DashboardRoute Match(string baseRelativePath, string homePath)
    {
        string path = baseRelativePath.Split('?', '#')[0].TrimEnd('/');
        if (path.Equals(homePath.Trim('/'), StringComparison.OrdinalIgnoreCase))
            return new(DashboardPage.Dashboard);
        string prefix = homePath == "/" ? "" : homePath.Trim('/') + "/";
        if (path.Equals(prefix + "signin", StringComparison.OrdinalIgnoreCase))
            return new(DashboardPage.SignIn);
        if (TryReadId(path, prefix + "jobs/", out string? jobId))
            return new(DashboardPage.Jobs, jobId);
        if (path.Equals(prefix + "recurring", StringComparison.OrdinalIgnoreCase))
            return new(DashboardPage.Recurring);
        if (path.Equals(prefix + "scheduled", StringComparison.OrdinalIgnoreCase))
            return new(DashboardPage.Scheduled);
        if (path.Equals(prefix + "servers", StringComparison.OrdinalIgnoreCase))
            return new(DashboardPage.Servers);
        if (TryReadId(path, prefix + "servers/", out string? serverId))
            return new(DashboardPage.ServerDetails, serverId);
        if (TryReadId(path, prefix + "recurring/", out string? scheduleId))
            return new(DashboardPage.Schedule, scheduleId);
        return new(DashboardPage.NotFound);
    }

    private static bool TryReadId(string path, string prefix, out string? id)
    {
        id = null;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        string segment = path[prefix.Length..];
        if (segment.Length == 0 || segment.Contains('/'))
            return false;
        id = Uri.UnescapeDataString(segment);
        return true;
    }
}
