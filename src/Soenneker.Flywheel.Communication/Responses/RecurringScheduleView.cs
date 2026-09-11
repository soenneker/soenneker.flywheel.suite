using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A recurring schedule with its interval and next due time in milliseconds.</summary>
public sealed record RecurringScheduleView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("interval")] long Interval,
    [property: JsonPropertyName("dueAt")] long DueAt,
    [property: JsonPropertyName("cron")] string? Cron = null,
    [property: JsonPropertyName("timeZoneId")] string TimeZoneId = "UTC",
    [property: JsonPropertyName("includeSeconds")] bool IncludeSeconds = false,
    [property: JsonPropertyName("lastExecutionStatus")] string? LastExecutionStatus = null);
