using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private sealed record StoredPolicy(
        [property: JsonPropertyName("maxConcurrency")] int MaxConcurrency = 0,
        [property: JsonPropertyName("rateLimit")] int RateLimit = 0,
        [property: JsonPropertyName("rateWindow")] long RateWindow = 60000);
}
