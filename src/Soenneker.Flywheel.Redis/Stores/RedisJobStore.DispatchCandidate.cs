using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private readonly record struct DispatchCandidate(RedisValue Id, string Name, int Priority, long DueAt);
}
