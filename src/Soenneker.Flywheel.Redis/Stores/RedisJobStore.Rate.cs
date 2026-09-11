using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private sealed record Rate(
        [property: JsonPropertyName("until")] long Until,
        [property: JsonPropertyName("count")] int Count);
}
