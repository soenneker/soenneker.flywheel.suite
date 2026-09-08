namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Job state transitions in a UTC bucket; historical buckets span five minutes and live buckets span one second.</summary>
public sealed record JobHistoryPoint(long Timestamp, int Scheduled, int Running, int Succeeded, int DeadLettered, int Cancelled = 0, int Waiting = 0, int Queued = 0)
{
    /// <summary>Server-recorded scheduled concurrency for a live bucket, or null when unavailable.</summary>
    public long? ScheduledCount { get; init; }
    /// <summary>Server-recorded running concurrency for a live bucket, or null when unavailable.</summary>
    public long? RunningCount { get; init; }
    /// <summary>Server-recorded eligible queue size for a live bucket, or null when unavailable.</summary>
    public long? QueuedCount { get; init; }
}
