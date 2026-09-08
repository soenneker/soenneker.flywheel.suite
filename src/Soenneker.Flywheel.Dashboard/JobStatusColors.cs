namespace Soenneker.Flywheel.Dashboard;

internal static class JobStatusColors
{
    private const string Scheduled = "#8b5cf6";
    private const string Running = "#0ea5e9";
    private const string Succeeded = "#10b981";
    private const string Failed = "#f97316";
    private const string Cancelling = "#fbbf24";
    private const string Default = "#94a3b8";
    private const string ForegroundPrefix = "color-mix(in srgb, ";
    private const string ForegroundSuffix = " 85%, var(--foreground, #0f172a))";

    public static string Accent(string state) => state switch
    {
        "Scheduled" => Scheduled,
        "Running" => Running,
        "Succeeded" => Succeeded,
        "DeadLettered" or "Failed" => Failed,
        "Cancelling" => Cancelling,
        _ => Default
    };

    public static string Foreground(string state) => state switch
    {
        "Scheduled" => ForegroundPrefix + Scheduled + ForegroundSuffix,
        "Running" => ForegroundPrefix + Running + ForegroundSuffix,
        "Succeeded" => ForegroundPrefix + Succeeded + ForegroundSuffix,
        "DeadLettered" or "Failed" => ForegroundPrefix + Failed + ForegroundSuffix,
        "Cancelling" => ForegroundPrefix + Cancelling + ForegroundSuffix,
        _ => ForegroundPrefix + Default + ForegroundSuffix
    };
}
