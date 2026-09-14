namespace Soenneker.Flywheel.Filesystem;

/// <summary>Configures a Flywheel Librarian database owned exclusively by one runtime.</summary>
public sealed class FlywheelFilesystemOptions
{
    /// <summary>Librarian database file on a local filesystem. Its directory must be writable for atomic file replacement.</summary>
    public string FilePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "flywheel.json");

    /// <summary>Retention for completed jobs and aggregate history, between five minutes and 365 days.</summary>
    public TimeSpan HistoryRetention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Whether maintenance retains completed records until history retention expires.</summary>
    public bool RetainCompletedJobs { get; set; } = true;
}
