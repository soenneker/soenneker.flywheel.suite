using System.Runtime.CompilerServices;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private const int ReadBatchSize = 128;

    private async Task<DispatchCandidate[]> ReadCandidates(IDatabase db, long now, string? applicationVersion,
        RedisValue revision, CancellationToken ct)
    {
        RedisValue[] ids = await db.SortedSetRangeByScoreAsync(Due, stop: now).WaitAsync(ct);
        if (ids.Length == 0) return [];
        var best = new Dictionary<string, DispatchCandidate>(StringComparer.Ordinal);
        await foreach ((RedisValue[] batch, RedisValue[] values) in ReadDispatchBatches(db, ids, revision, ct))
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].IsNull) continue;
                DispatchMetadata metadata = ReadIndexedDispatchMetadata(values[i]);
                if (metadata.State != 0) continue;
                if (metadata.ApplicationVersion is not null &&
                    !string.Equals(metadata.ApplicationVersion, applicationVersion, StringComparison.Ordinal)) continue;
                var candidate = new DispatchCandidate(batch[i], metadata.Name, metadata.Priority, metadata.DueAt);
                if (!best.TryGetValue(metadata.Name, out DispatchCandidate previous) || DispatchComparer.Instance.Compare(candidate, previous) < 0)
                    best[metadata.Name] = candidate;
            }
        }
        var result = new DispatchCandidate[best.Count];
        best.Values.CopyTo(result, 0);
        Array.Sort(result, DispatchComparer.Instance);
        return result;
    }

    private async Task<Dictionary<string, int>> ReadActiveCounts(IDatabase db, long now,
        RedisValue revision, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        RedisValue[] ids = await db.SortedSetRangeByScoreAsync(Running, start: now, exclude: Exclude.Start).WaitAsync(ct);
        await foreach ((RedisValue[] _, RedisValue[] values) in ReadDispatchBatches(db, ids, revision, ct))
        {
            foreach (RedisValue value in values)
            {
                if (value.IsNull) continue;
                DispatchMetadata metadata = ReadIndexedDispatchMetadata(value);
                if (metadata.State == 1) counts[metadata.Name] = counts.GetValueOrDefault(metadata.Name) + 1;
            }
        }
        return counts;
    }

    private async IAsyncEnumerable<(RedisValue[] Ids, RedisValue[] Values)> ReadDispatchBatches(IDatabase db,
        RedisValue[] ids, RedisValue revision, [EnumeratorCancellation] CancellationToken ct)
    {
        // Bound both response size and outstanding metadata reads.
        const int depth = 4;
        for (int offset = 0; offset < ids.Length;)
        {
            int count = Math.Min(depth, (ids.Length - offset + ReadBatchSize - 1) / ReadBatchSize);
            var batches = new RedisValue[count][];
            var reads = new Task<RedisValue[]>[count];
            for (int i = 0; i < count; i++)
            {
                int length = Math.Min(ReadBatchSize, ids.Length - offset);
                RedisValue[] batch = ids.Length <= ReadBatchSize ? ids : ids.AsSpan(offset, length).ToArray();
                batches[i] = batch;
                reads[i] = db.HashGetAsync(Dispatch, batch).WaitAsync(ct);
                offset += length;
            }
            await Task.WhenAll(reads);
            for (int i = 0; i < count; i++)
            {
                RedisValue[] values = reads[i].Result;
                // A concurrent terminal transition can remove metadata after the ID snapshot. A stable
                // missing entry is invalid storage; never silently skip work or reconstruct old formats.
                if (Array.Exists(values, static value => value.IsNull) &&
                    await db.StringGetAsync(Revision).WaitAsync(ct) == revision)
                    throw new InvalidOperationException("Required dispatch metadata is missing. All jobs must be written by the current Flywheel storage implementation.");
                yield return (batches[i], values);
            }
        }
    }

    private async Task<StoredPolicy[]> ReadPolicies(IDatabase db, DispatchCandidate[] candidates, CancellationToken ct)
    {
        var names = new RedisValue[candidates.Length];
        for (var i = 0; i < candidates.Length; i++)
            names[i] = candidates[i].Name;

        RedisValue[] values = await db.HashGetAsync(Policies, names).WaitAsync(ct);
        var policies = new StoredPolicy[values.Length];
        for (var i = 0; i < values.Length; i++)
            policies[i] = Decode<StoredPolicy>(values[i]) ?? DefaultPolicy;
        return policies;
    }

    private async Task<Rate?[]> ReadRates(IDatabase db, DispatchCandidate[] candidates, StoredPolicy[] policies, CancellationToken ct)
    {
        var rateCount = 0;
        for (var i = 0; i < policies.Length; i++)
            if (policies[i].RateLimit > 0) rateCount++;

        if (rateCount == 0) return [];

        var rates = new Rate?[policies.Length];
        var names = new RedisValue[rateCount];
        var indexes = new int[rateCount];
        for (int candidateIndex = 0, rateIndex = 0; candidateIndex < policies.Length; candidateIndex++)
        {
            if (policies[candidateIndex].RateLimit <= 0) continue;
            names[rateIndex] = candidates[candidateIndex].Name;
            indexes[rateIndex++] = candidateIndex;
        }

        RedisValue[] values = await db.HashGetAsync(Rates, names).WaitAsync(ct);
        for (var i = 0; i < values.Length; i++)
            rates[indexes[i]] = Decode<Rate>(values[i]);
        return rates;
    }

    private static RedisValue[] GetReadBatch(RedisValue[] ids, int offset, ref RedisValue[]? buffer)
    {
        if (ids.Length <= ReadBatchSize)
            return ids;

        int count = Math.Min(ReadBatchSize, ids.Length - offset);
        if (buffer is null || buffer.Length != count)
            buffer = new RedisValue[count];

        ids.AsSpan(offset, count).CopyTo(buffer);
        return buffer;
    }

}
