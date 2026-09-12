using Soenneker.Flywheel.Communication.Responses;
using StackExchange.Redis;
using System.Buffers.Text;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private const int LiveActivityBucketCount = 61;
    // All keys are declared and share a cluster slot. Atomic reads/writes avoid optimistic retries. The
    // revision proves idle intervals, and hosts share samples unless new work invalidates an empty sample.
    private const string RecordLiveActivityScript = """
        local clock = redis.call('TIME')
        local second = tonumber(clock[1])
        local previous = redis.call('HMGET', KEYS[3], 'latest', 'active', 'empty-revision', 'empty-from')
        local revision = nil
        if previous[1] == clock[1] then
            if previous[2] == '1' then return 1 end
            revision = redis.call('GET', KEYS[4]) or ''
            if previous[3] == revision then return 0 end
        end
        local now = second * 1000 + math.floor(tonumber(clock[2]) / 1000)
        local running = redis.call('ZCARD', KEYS[1])
        local queued = redis.call('ZCOUNT', KEYS[2], '-inf', now)
        local scheduled = redis.call('ZCARD', KEYS[2]) - queued
        local active = running + queued + scheduled > 0 and 1 or 0
        local values = {'latest', clock[1], 'active', active, 'empty-revision', '', 'empty-from', ''}
        local first = second
        if active == 0 then
            revision = revision or redis.call('GET', KEYS[4]) or ''
            local from = second
            if previous[2] == '0' and previous[3] == revision and tonumber(previous[1]) <= second then
                first = math.max(tonumber(previous[1]) + 1, second - 60)
                from = tonumber(previous[4])
            end
            values[6] = revision
            values[8] = tostring(from)
        end
        for stamp = first, second do
            values[#values + 1] = tostring(stamp % 61)
            values[#values + 1] = tostring(stamp) .. '|' .. scheduled .. '|' .. running .. '|' .. queued
        end
        redis.call('HSET', KEYS[3], unpack(values))
        redis.call('EXPIREAT', KEYS[3], second + 61)
        return active
        """;
    private static DateTime LiveActivityExpiry(long timestamp) =>
        DateTimeOffset.FromUnixTimeMilliseconds((timestamp / 1000 + LiveActivityBucketCount) * 1000).UtcDateTime;

    internal async Task<bool> SampleLiveActivity(CancellationToken cancellationToken)
    {
        IDatabase db = await Database(cancellationToken);
        return (long)await db.ScriptEvaluateAsync(RecordLiveActivityScript, _liveSampleKeys).WaitAsync(cancellationToken) != 0;
    }

    public async Task<IReadOnlyList<JobHistoryPoint>> GetLiveActivity(CancellationToken cancellationToken = default)
    {
        IDatabase db = await Database(cancellationToken);
        long last = await Time(db, cancellationToken) / 1000;
        IBatch batch = db.CreateBatch();
        RedisValue[] fields = [0, 1, 2, 3];
        var reads = new Task<RedisValue[]>[LiveActivityBucketCount + 2];
        var sampleFields = new RedisValue[LiveActivityBucketCount + 2];
        for (var index = 0; index < LiveActivityBucketCount; index++)
        {
            long second = last - (LiveActivityBucketCount - 1) + index;
            reads[index] = batch.HashGetAsync(_prefix + "activity:" + second, fields);
            sampleFields[index] = second % LiveActivityBucketCount;
        }
        sampleFields[LiveActivityBucketCount] = "empty-from";
        sampleFields[LiveActivityBucketCount + 1] = "empty-revision";
        reads[LiveActivityBucketCount] = batch.HashGetAsync(LiveSamples, sampleFields);
        reads[LiveActivityBucketCount + 1] = batch.StringGetAsync([Revision]);
        batch.Execute();
        RedisValue[][] values = await Task.WhenAll(reads).WaitAsync(cancellationToken);
        RedisValue[] samples = values[LiveActivityBucketCount];
        RedisValue revision = values[LiveActivityBucketCount + 1][0];
        // The initial TIME precedes these reads. An unchanged revision proves the interval up to that time
        // stayed empty. A lifecycle mutation invalidates this proof, yielding unknowns.
        bool empty = samples[LiveActivityBucketCount].TryParse(out long emptyFrom) &&
                     samples[LiveActivityBucketCount + 1] == (revision.IsNull ? (RedisValue)"" : revision);
        var points = new List<JobHistoryPoint>(LiveActivityBucketCount);
        for (var index = 0; index < LiveActivityBucketCount; index++)
        {
            RedisValue[] value = values[index];
            long second = last - (LiveActivityBucketCount - 1) + index;
            bool sampled = TryReadLiveSample(samples[index], second,
                out long scheduled, out long running, out long queued);
            if (!sampled && empty && second >= emptyFrom)
            {
                sampled = true;
                scheduled = running = queued = 0;
            }
            points.Add(new(second * 1000, Count(value[0]), Count(value[1]), Count(value[2]), Count(value[3]))
            {
                ScheduledCount = sampled ? scheduled : null,
                RunningCount = sampled ? running : null,
                QueuedCount = sampled ? queued : null
            });
        }
        return points;

        static int Count(RedisValue value) => value.IsNull ? 0 : (int)value;
    }

    private static bool TryReadLiveSample(RedisValue value, long second, out long scheduled, out long running, out long queued)
    {
        scheduled = running = queued = 0;
        if (value.IsNull) return false;
        ReadOnlySpan<byte> data = ((ReadOnlyMemory<byte>)value).Span;
        return ReadPart(ref data, out long timestamp) && timestamp == second &&
               ReadPart(ref data, out scheduled) && ReadPart(ref data, out running) &&
               Utf8Parser.TryParse(data, out queued, out int consumed) && consumed == data.Length;

        static bool ReadPart(ref ReadOnlySpan<byte> data, out long number)
        {
            if (!Utf8Parser.TryParse(data, out number, out int consumed) || consumed >= data.Length || data[consumed] != (byte)'|')
                return false;
            data = data[(consumed + 1)..];
            return true;
        }
    }
}
