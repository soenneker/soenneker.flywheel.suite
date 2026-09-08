using Soenneker.Flywheel.Core.Dtos;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private sealed record Schedule(JobRecord Job, long Interval, long Sequence = 0,
        string? Cron = null, string TimeZoneId = "UTC", bool IncludeSeconds = false);
}
