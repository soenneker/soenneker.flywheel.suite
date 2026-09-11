using System.Text.Json;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    private const int ReadBatchSize = 128;

    private static DispatchMetadata ReadDispatchMetadata(RedisValue value)
    {
        var reader = new Utf8JsonReader(((ReadOnlyMemory<byte>)value).Span);
        string? name = null;
        string? applicationVersion = null;
        int state = 0, priority = 1;
        long dueAt = 0;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            if (reader.ValueTextEquals("name"u8))
            {
                reader.Read();
                name = reader.GetString();
            }
            else if (reader.ValueTextEquals("applicationVersion"u8))
            {
                reader.Read();
                applicationVersion = reader.GetString();
            }
            else if (reader.ValueTextEquals("state"u8))
            {
                reader.Read();
                state = reader.GetInt32();
            }
            else if (reader.ValueTextEquals("dueAt"u8))
            {
                reader.Read();
                dueAt = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("policy"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Job policy must be an object.");
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    bool isPriority = reader.ValueTextEquals("priority"u8);
                    reader.Read();
                    if (isPriority) priority = reader.GetInt32();
                    else reader.Skip();
                }
            }
            else
            {
                reader.Read();
                reader.Skip(); // Do not allocate payload, policy, token or error strings during dispatch selection.
            }
        }
        return new(name ?? throw new JsonException("Job name is missing."), state, priority, dueAt, applicationVersion);
    }

    private async Task<DispatchCandidate[]> ReadCandidates(IDatabase db, long now, string? applicationVersion, CancellationToken ct)
    {
        RedisValue[] ids = await db.SortedSetRangeByScoreAsync(Due, stop: now).WaitAsync(ct);
        if (ids.Length == 0) return [];
        var best = new Dictionary<string, DispatchCandidate>(StringComparer.Ordinal);
        RedisValue[]? batchBuffer = null;
        for (int offset = 0; offset < ids.Length; offset += ReadBatchSize)
        {
            RedisValue[] batch = GetReadBatch(ids, offset, ref batchBuffer);
            RedisValue[] values = await db.HashGetAsync(Jobs, batch).WaitAsync(ct);
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].IsNull) continue;
                DispatchMetadata metadata = ReadDispatchMetadata(values[i]);
                if (metadata.State != 0) continue;
                if (metadata.ApplicationVersion is not null &&
                    !string.Equals(metadata.ApplicationVersion, applicationVersion, StringComparison.Ordinal)) continue;
                var candidate = new DispatchCandidate(batch[i], values[i], metadata.Name, metadata.Priority, metadata.DueAt);
                if (!best.TryGetValue(metadata.Name, out DispatchCandidate previous) || DispatchComparer.Instance.Compare(candidate, previous) < 0)
                    best[metadata.Name] = candidate;
            }
        }
        var result = new DispatchCandidate[best.Count];
        best.Values.CopyTo(result, 0);
        Array.Sort(result, DispatchComparer.Instance);
        return result;
    }

    private async Task<Dictionary<string, int>> ReadActiveCounts(IDatabase db, long now, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        RedisValue[] ids = await db.SortedSetRangeByScoreAsync(Running, start: now, exclude: Exclude.Start).WaitAsync(ct);
        RedisValue[]? batchBuffer = null;
        for (int offset = 0; offset < ids.Length; offset += ReadBatchSize)
        {
            RedisValue[] batch = GetReadBatch(ids, offset, ref batchBuffer);
            RedisValue[] values = await db.HashGetAsync(Jobs, batch).WaitAsync(ct);
            foreach (RedisValue value in values)
            {
                if (value.IsNull) continue;
                DispatchMetadata metadata = ReadDispatchMetadata(value);
                if (metadata.State == 1) counts[metadata.Name] = counts.GetValueOrDefault(metadata.Name) + 1;
            }
        }
        return counts;
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
