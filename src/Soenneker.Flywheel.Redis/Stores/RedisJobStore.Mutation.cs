using Soenneker.Redis.Util.Atomics;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private readonly record struct Mutation(RedisAtomicTransaction Transaction, long Now, RedisChannel Channel);
}
