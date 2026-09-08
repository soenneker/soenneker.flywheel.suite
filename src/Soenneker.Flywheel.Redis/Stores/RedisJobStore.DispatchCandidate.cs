using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private readonly record struct DispatchCandidate(RedisValue Id, RedisValue Data, string Name, int Priority, long DueAt);
}
