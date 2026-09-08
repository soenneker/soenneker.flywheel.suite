namespace Soenneker.Flywheel.Dashboard.Pages.Schedules.Shared;

internal static class ScheduleFormatting
{
    public static string IntervalLabel(long milliseconds)
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(milliseconds);
        if (milliseconds % 86400000 == 0) return $"{interval.TotalDays:0}d";
        if (milliseconds % 3600000 == 0) return $"{interval.TotalHours:0}h";
        if (milliseconds % 60000 == 0) return $"{interval.TotalMinutes:0}m";
        return $"{interval.TotalSeconds:0.###}s";
    }

}
