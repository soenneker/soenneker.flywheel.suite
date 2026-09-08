namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    // Numeric window representation is compatible with existing persisted method policies.
    private sealed record StoredPolicy(int MaxConcurrency = 0, int RateLimit = 0, long RateWindow = 60000);
}
