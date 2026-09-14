namespace Soenneker.Flywheel.Memory;

/// <summary>Configures storage retained by one in-process Flywheel provider.</summary>
public sealed class FlywheelMemoryOptions
{
    /// <summary>Retention for completed jobs and aggregate history, between five minutes and 365 days.</summary>
    public TimeSpan HistoryRetention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Whether maintenance retains completed records until history retention expires.</summary>
    public bool RetainCompletedJobs { get; set; } = true;
}
