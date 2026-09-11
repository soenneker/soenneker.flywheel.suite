using System.Text.Json.Serialization;
using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private sealed record Schedule(
        [property: JsonPropertyName("job")] JobRecord Job,
        [property: JsonPropertyName("interval")] long Interval,
        [property: JsonPropertyName("sequence")] long Sequence = 0,
        [property: JsonPropertyName("cron")] string? Cron = null,
        [property: JsonPropertyName("timeZoneId")] string TimeZoneId = "UTC",
        [property: JsonPropertyName("includeSeconds")] bool IncludeSeconds = false,
        [property: JsonPropertyName("lastExecutionId")] string? LastExecutionId = null,
        [property: JsonPropertyName("lastExecutionStatus")] string? LastExecutionStatus = null);
}
