namespace Soenneker.Flywheel.Core.Options;

/// <summary>Optional durable log archival, independent of dashboard history retention.</summary>
public sealed class LogArchiveOptions
{
    /// <summary>Enables archival of newly accepted log entries. Defaults to false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Maximum UTF-8 JSON bytes per archive file, excluding provider-specific envelopes. Defaults to 200 MiB.</summary>
    public int MaxFileSizeBytes { get; set; } = 200 * 1024 * 1024;

    /// <summary>UTC-aligned rotation interval, measured from the Unix epoch. The daily default rolls at midnight UTC,
    /// including when a file was opened shortly before midnight. Size rotation does not reset this boundary.</summary>
    public TimeSpan RotationInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Interval of the dashboard-visible recurring archive export job. Each run exports one ready file.
    /// Failed runs remain visible and pending files are retried by subsequent runs.</summary>
    public TimeSpan ExportInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum duration of an individual storage export.</summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        // One maximally escaped accepted entry must always fit.
        if (MaxFileSizeBytes < 32768 || MaxFileSizeBytes > 1024 * 1024 * 1024 ||
            RotationInterval < TimeSpan.FromSeconds(1) || RotationInterval > TimeSpan.FromDays(31) ||
            ExportInterval < TimeSpan.FromSeconds(1) || ExportInterval > TimeSpan.FromHours(1) ||
            ExportTimeout < TimeSpan.FromSeconds(1) || ExportTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(LogArchiveOptions));
    }
}
