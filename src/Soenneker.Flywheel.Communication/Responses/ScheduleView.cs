using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Registered recurring schedules and pending executions, ordered by next due time.</summary>
public sealed record ScheduleView(
    [property: JsonPropertyName("recurring")] List<RecurringScheduleView> Recurring,
    [property: JsonPropertyName("scheduled")] List<JobView> Scheduled);
