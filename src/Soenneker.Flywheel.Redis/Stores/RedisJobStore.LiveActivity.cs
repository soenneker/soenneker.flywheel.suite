using Soenneker.Flywheel.Communication.Responses;
using StackExchange.Redis;
using Soenneker.Redis.Util.Atomics;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private const int LiveActivityBucketCount = 61;
    private static DateTime LiveActivityExpiry(long timestamp) =>
        DateTimeOffset.FromUnixTimeMilliseconds((timestamp / 1000 + LiveActivityBucketCount) * 1000).UtcDateTime;

    internal async Task RecordLiveActivity(CancellationToken cancellationToken)
    {
        IDatabase db = await Database(cancellationToken);
        for (int attempt = 0;; attempt++)
        {
            RedisValue revision = await db.StringGetAsync(Revision).WaitAsync(cancellationToken);
            long now = await Time(db, cancellationToken);
            Task<long> running = db.SortedSetLengthAsync(Running);
            Task<long> scheduled = db.SortedSetLengthAsync(Due, now, double.PositiveInfinity, Exclude.Start);
            Task<long> queued = db.SortedSetLengthAsync(Due, double.NegativeInfinity, now);
            await Task.WhenAll(running, scheduled, queued).WaitAsync(cancellationToken);
            var transaction = new RedisAtomicTransaction(db);
            transaction.Require(RedisAtomics.StringMatches(Revision, revision));
            RedisKey key = _prefix + "activity:" + now / 1000;
            transaction.Queue(t => t.HashSetAsync(key,
            [
                new HashEntry("scheduled-count", scheduled.Result),
                new HashEntry("running-count", running.Result),
                new HashEntry("queued-count", queued.Result)
            ]));
            transaction.Queue(t => t.KeyExpireAsync(key, LiveActivityExpiry(now)));
            if (await transaction.Execute(cancellationToken)) return;
            await Retry(attempt, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<JobHistoryPoint>> GetLiveActivity(CancellationToken cancellationToken = default)
    {
        IDatabase db = await Database(cancellationToken);
        long last = await Time(db, cancellationToken) / 1000;
        IBatch batch = db.CreateBatch();
        RedisValue[] fields = [0, 1, 2, 3, "scheduled-count", "running-count", "queued-count"];
        var reads = new Task<RedisValue[]>[LiveActivityBucketCount];
        for (var index = 0; index < reads.Length; index++)
            reads[index] = batch.HashGetAsync(_prefix + "activity:" + (last - (LiveActivityBucketCount - 1) + index), fields);
        batch.Execute();
        RedisValue[][] values = await Task.WhenAll(reads).WaitAsync(cancellationToken);
        var points = new List<JobHistoryPoint>(LiveActivityBucketCount);
        for (var index = 0; index < values.Length; index++)
        {
            RedisValue[] value = values[index];
            points.Add(new((last - (LiveActivityBucketCount - 1) + index) * 1000, Count(value[0]), Count(value[1]), Count(value[2]), Count(value[3]))
            {
                ScheduledCount = value[4].IsNull ? null : (long)value[4],
                RunningCount = value[5].IsNull ? null : (long)value[5],
                QueuedCount = value[6].IsNull ? null : (long)value[6]
            });
        }
        return points;

        static int Count(RedisValue value) => value.IsNull ? 0 : (int)value;
    }
}
