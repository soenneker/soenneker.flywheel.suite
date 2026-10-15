using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Registered recurring schedules and pending executions, ordered by next due time.</summary>
public sealed record ScheduleView(
    [property: JsonPropertyName("recurring")] List<RecurringScheduleView> Recurring,
    [property: JsonPropertyName("scheduled")] List<JobView> Scheduled)
{
    /// <summary>Exact counts across all pending executions, or null when unsupported by the store.</summary>
    public JobQueueCounts? QueueCounts { get; init; }

    /// <summary>Total recurring definitions, independently of the returned list.</summary>
    public long? RecurringCount { get; init; }
}
