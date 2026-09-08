using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Redis.Semaphores;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private readonly record struct Ownership(JobRecord? Job, RedisValue Raw, RedisKey LeaseKey, RedisSemaphorePermit? Permit);
}
