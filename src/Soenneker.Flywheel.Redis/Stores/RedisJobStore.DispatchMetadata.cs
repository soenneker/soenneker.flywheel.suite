namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private readonly record struct DispatchMetadata(string Name, int State, int Priority, long DueAt);
}
