using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Current search page and optional summary for a versioned live subscription.</summary>
public sealed record LiveBoard(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("items")] List<JobView> Items,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("history")] List<JobHistoryPoint>? History,
    [property: JsonPropertyName("schedules")] ScheduleView? Schedules,
    [property: JsonPropertyName("runningCount")] long? RunningCount = null,
    [property: JsonPropertyName("serverCount")] int? ServerCount = null,
    [property: JsonPropertyName("totalWorkers")] int? TotalWorkers = null,
    [property: JsonPropertyName("liveActivity")] List<JobHistoryPoint>? LiveActivity = null,
    [property: JsonPropertyName("recurringCount")] long? RecurringCount = null);
