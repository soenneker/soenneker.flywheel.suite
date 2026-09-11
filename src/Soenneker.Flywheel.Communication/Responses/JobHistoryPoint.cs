using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Job state transitions in a UTC bucket; historical buckets span five minutes and live buckets span one second.</summary>
public sealed record JobHistoryPoint(
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("scheduled")] int Scheduled,
    [property: JsonPropertyName("running")] int Running,
    [property: JsonPropertyName("succeeded")] int Succeeded,
    [property: JsonPropertyName("deadLettered")] int DeadLettered,
    [property: JsonPropertyName("cancelled")] int Cancelled = 0,
    [property: JsonPropertyName("waiting")] int Waiting = 0,
    [property: JsonPropertyName("queued")] int Queued = 0)
{
    /// <summary>Server-recorded scheduled concurrency for a live bucket, or null when unavailable.</summary>
    [JsonPropertyName("scheduledCount")]
    public long? ScheduledCount { get; init; }
    /// <summary>Server-recorded running concurrency for a live bucket, or null when unavailable.</summary>
    [JsonPropertyName("runningCount")]
    public long? RunningCount { get; init; }
    /// <summary>Server-recorded eligible queue size for a live bucket, or null when unavailable.</summary>
    [JsonPropertyName("queuedCount")]
    public long? QueuedCount { get; init; }
}
