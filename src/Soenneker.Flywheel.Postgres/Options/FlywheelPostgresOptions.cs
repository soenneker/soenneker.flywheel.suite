namespace Soenneker.Flywheel.Postgres;

/// <summary>Configures PostgreSQL storage shared by Flywheel workers.</summary>
public sealed class FlywheelPostgresOptions
{
    /// <summary>PostgreSQL connection string used by the storage provider.</summary>
    public string ConnectionString { get; set; } = "";
    /// <summary>Logical Librarian database key. Workers sharing jobs must use the same namespace.</summary>
    public string Namespace { get; set; } = "flywheel";
    /// <summary>Amount of aggregate activity and completed-job history to retain, from five minutes to 365 days.</summary>
    public TimeSpan HistoryRetention { get; set; } = TimeSpan.FromDays(1);
    /// <summary>Whether terminal jobs and their logs remain available during the retention period.</summary>
    public bool RetainCompletedJobs { get; set; } = true;
}
