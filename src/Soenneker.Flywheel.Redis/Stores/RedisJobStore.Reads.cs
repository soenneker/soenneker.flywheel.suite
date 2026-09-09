using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Responses;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private async Task<JobRecord[]> ReadJobs(IDatabase db, RedisValue[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var jobs = new JobRecord[ids.Length];
        int count = 0;
        RedisValue[]? batchBuffer = null;
        for (int offset = 0; offset < ids.Length; offset += ReadBatchSize)
        {
            RedisValue[] batch = GetReadBatch(ids, offset, ref batchBuffer);
            RedisValue[] values = await db.HashGetAsync(Jobs, batch).WaitAsync(ct);
            foreach (RedisValue value in values)
                if (Decode<JobRecord>(value) is { } job) jobs[count++] = job;
        }
        if (count != jobs.Length) Array.Resize(ref jobs, count);
        return jobs;
    }

    public async Task<JobRecord?> Get(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            throw new ArgumentException("Invalid job ID.", nameof(id));
        IDatabase db = await Database(cancellationToken);
        return Decode<JobRecord>(await db.HashGetAsync(Jobs, id).WaitAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<JobRecord>> List(int offset = 0, int count = 50,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || count is < 1 or > 200)
            throw new ArgumentOutOfRangeException();
        IDatabase db = await Database(cancellationToken);
        RedisValue[] ids = await db.SortedSetRangeByRankAsync(All, offset, (long)offset + count - 1, Order.Descending)
                                   .WaitAsync(cancellationToken);
        return await ReadJobs(db, ids, cancellationToken);
    }

    public async Task<IReadOnlyList<JobRecord>> ListScheduled(int count = 50,
        CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(count));
        IDatabase db = await Database(cancellationToken);
        RedisValue[] ids = await db.SortedSetRangeByRankAsync(Due, 0, count - 1).WaitAsync(cancellationToken);
        JobRecord[] jobs = await ReadJobs(db, ids, cancellationToken);
        int scheduled = 0;
        foreach (JobRecord job in jobs)
            if (job.State == JobState.Scheduled) jobs[scheduled++] = job;
        if (scheduled != jobs.Length) Array.Resize(ref jobs, scheduled);
        return jobs;
    }

    private async Task<JobRecord[]> SearchBatch(IDatabase db, string? cursor, CancellationToken ct)
    {
        // Validate the revision around rank + range reads: a newer insertion must not shift the page boundary.
        for (var attempt = 0;; attempt++)
        {
            RedisValue before = await db.StringGetAsync(Revision).WaitAsync(ct);
            long start = 0;
            if (cursor is not null)
            {
                long? rank = await db.SortedSetRankAsync(All, cursor, Order.Descending).WaitAsync(ct);
                if (rank is null)
                    throw new InvalidOperationException("Search cursor no longer exists.");
                start = rank.Value + 1;
            }

            RedisValue[] ids = await db.SortedSetRangeByRankAsync(All, start, start + 99, Order.Descending).WaitAsync(ct);
            JobRecord[] jobs = await ReadJobs(db, ids, ct);
            if (await db.StringGetAsync(Revision).WaitAsync(ct) == before)
                return jobs;
            await Retry(attempt, ct);
        }
    }

    public async Task<JobSearchResult> Search(string? query, int offset = 0, int count = 50,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || count is < 1 or > 200 || query?.Length > 200)
            throw new ArgumentOutOfRangeException();
        query = query?.Trim() ?? "";
        IDatabase db = await Database(cancellationToken);
        if (query.Length == 0)
        {
            var total = checked((int)await db.SortedSetLengthAsync(All).WaitAsync(cancellationToken));
            return new(await List(offset, count, cancellationToken), total);
        }

        long now = await Time(db, cancellationToken);
        var items = new List<JobRecord>(count);
        var matches = 0;
        string? cursor = null;
        while (true)
        {
            JobRecord[] batch = await SearchBatch(db, cursor, cancellationToken);
            foreach (JobRecord job in batch)
            {
                if (!job.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !job.Id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !job.DisplayState(now).Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !(job.Owner?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    continue;
                if (matches++ >= offset && items.Count < count)
                    items.Add(job);
            }

            if (batch.Length < 100)
                break;
            cursor = batch[^1].Id;
        }

        return new(items, matches);
    }

    public async Task<JobSearchResult> Search(string? query, DateTimeOffset startAt, DateTimeOffset endAt, int offset = 0, int count = 50,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || count is < 1 or > 200 || query?.Length > 200 || startAt >= endAt)
            throw new ArgumentOutOfRangeException();

        query = query?.Trim() ?? "";
        IDatabase db = await Database(cancellationToken);
        long minimum = startAt.ToUnixTimeMilliseconds();
        long maximum = endAt.ToUnixTimeMilliseconds();
        long now = await Time(db, cancellationToken);
        var items = new List<JobRecord>(count);
        var matches = 0;
        string? cursor = null;
        while (true)
        {
            JobRecord[] batch = await SearchBatch(db, cursor, cancellationToken);
            foreach (JobRecord job in batch)
            {
                if (job.UpdatedAt < minimum || job.UpdatedAt >= maximum || query.Length != 0 &&
                    !job.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !job.Id.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !job.DisplayState(now).Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !(job.Owner?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    continue;
                if (matches++ >= offset && items.Count < count)
                    items.Add(job);
            }

            if (batch.Length < 100)
                break;
            cursor = batch[^1].Id;
        }

        return new(items, matches);
    }

    public async Task<IReadOnlyList<JobHistoryPoint>> GetHistory(DateTimeOffset startAt, DateTimeOffset endAt,
        CancellationToken cancellationToken = default)
    {
        IDatabase db = await Database(cancellationToken);
        long now = await Time(db, cancellationToken);
        long requestedStart = startAt.ToUnixTimeMilliseconds();
        long requestedEnd = endAt.ToUnixTimeMilliseconds();
        if (requestedStart >= requestedEnd || requestedEnd > now + 300000 || requestedStart < requestedEnd - HistoryRetention.TotalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(startAt));
        long first = requestedStart / 300000 * 300000;
        long last = (requestedEnd - 1) / 300000 * 300000;
        int bucketCount = checked((int)((last - first) / 300000 + 1));
        var fields = new RedisValue[checked(1 + bucketCount * 4)];
        fields[0] = "started";
        int fieldIndex = 1;
        for (long bucket = first; bucket <= last; bucket += 300000)
        for (var state = 0; state < 4; state++)
            fields[fieldIndex++] = $"{bucket}:{state}";
        RedisValue[] values = await db.HashGetAsync(History, fields).WaitAsync(cancellationToken);
        var points = new List<JobHistoryPoint>(bucketCount);
        for (var index = 1; index < values.Length; index += 4)
            points.Add(new(first + (index - 1) / 4 * 300000L, values[index].IsNull ? 0 : (int)values[index],
                values[index + 1].IsNull ? 0 : (int)values[index + 1],
                values[index + 2].IsNull ? 0 : (int)values[index + 2],
                values[index + 3].IsNull ? 0 : (int)values[index + 3]));
        long started = values[0].IsNull ? last + 300000 : (long)values[0];
        if (started <= first)
            return points;
        // New namespaces already record every transition. Avoid scanning their entire retained history for
        // legacy records that cannot exist: a record cannot have been updated before it was created.
        SortedSetEntry[] oldest = await db.SortedSetRangeByRankWithScoresAsync(All, 0, 0).WaitAsync(cancellationToken);
        if (oldest.Length == 0 || oldest[0].Score >= started) return points;
        // Preserve the legacy snapshot fallback without counting transitions recorded by this store twice.
        string? cursor = null;
        while (true)
        {
            JobRecord[] jobs = await SearchBatch(db, cursor, cancellationToken);
            foreach (JobRecord job in jobs)
            {
                if (job.UpdatedAt < first || job.UpdatedAt >= started)
                    continue;
                var index = (int)((job.UpdatedAt - first) / 300000);
                if (index >= points.Count)
                    continue;
                JobHistoryPoint point = points[index];
                points[index] = job.State.Value switch
                {
                    0 => point with { Scheduled = point.Scheduled + 1 },
                    1 => point with { Running = point.Running + 1 },
                    2 => point with { Succeeded = point.Succeeded + 1 },
                    3 => point with { DeadLettered = point.DeadLettered + 1 },
                    _ => point
                };
            }

            if (jobs.Length < 100)
                break;
            cursor = jobs[^1].Id;
        }

        return points;
    }

    public Task<IReadOnlyList<JobHistoryPoint>> GetHistory(CancellationToken cancellationToken = default)
    {
        DateTimeOffset endAt = DateTimeOffset.UtcNow;
        DateTimeOffset startAt = endAt - (HistoryRetention < TimeSpan.FromDays(1) ? HistoryRetention : TimeSpan.FromDays(1));
        return GetHistory(startAt, endAt, cancellationToken);
    }
}
