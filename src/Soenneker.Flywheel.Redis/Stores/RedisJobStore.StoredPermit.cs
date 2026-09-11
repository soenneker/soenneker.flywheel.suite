using System.Text.Json.Serialization;
using Soenneker.Redis.Semaphores;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private sealed record StoredPermit(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("token")] string Token)
    {
        public RedisSemaphorePermit ToPermit() => new(Key, Token);
    }
}
