using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Responses;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    public async Task<IReadOnlyList<JobHistoryPoint>> GetSearchHistory(string? query, DateTimeOffset? startAt, DateTimeOffset? endAt,
        CancellationToken cancellationToken = default)
    {
        if (query?.Length > 200 || startAt.HasValue != endAt.HasValue || startAt >= endAt)
            throw new ArgumentOutOfRangeException(nameof(query));
        query = query?.Trim() ?? "";
        IDatabase db = await Database(cancellationToken);
        long now = await Time(db, cancellationToken);
        var buckets = new SortedDictionary<long, JobHistoryPoint>();
        string? cursor = null;
        while (true)
        {
            JobRecord[] batch = await SearchBatch(db, cursor, cancellationToken);
            foreach (JobRecord job in batch)
            {
                if (startAt is { } start && job.UpdatedAt < start.ToUnixTimeMilliseconds() ||
                    endAt is { } end && job.UpdatedAt >= end.ToUnixTimeMilliseconds() ||
                    query.Length != 0 && !job.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !job.Id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !job.DisplayState(now).Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !(job.Owner?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                long timestamp = job.UpdatedAt / 300000 * 300000;
                JobHistoryPoint point = buckets.GetValueOrDefault(timestamp) ?? new(timestamp, 0, 0, 0, 0);
                buckets[timestamp] = job.State.Value switch
                {
                    0 when job.DisplayState(now) == "Queued" => point with { Queued = point.Queued + 1 },
                    0 => point with { Scheduled = point.Scheduled + 1 },
                    1 => point with { Running = point.Running + 1 },
                    2 => point with { Succeeded = point.Succeeded + 1 },
                    3 => point with { DeadLettered = point.DeadLettered + 1 },
                    4 => point with { Cancelled = point.Cancelled + 1 },
                    5 => point with { Waiting = point.Waiting + 1 },
                    _ => point
                };
            }
            if (batch.Length < 100) break;
            cursor = batch[^1].Id;
        }
        return buckets.Values.ToList();
    }
}
