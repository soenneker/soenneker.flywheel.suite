namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private sealed record Rate(long Until, int Count);
}
