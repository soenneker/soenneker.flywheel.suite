using Soenneker.Extensions.ValueTask;
using Soenneker.Hashing.Sha256;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Utils.Json;
using Soenneker.Utils.PooledStringBuilders;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

// Only writes in the current attempt are staged. Reads always use the provider's documents.
internal sealed class LibrarianTable<TKey, TValue>(string name) : ILibrarianTable
    where TKey : notnull
{
    private static readonly Sha256HashingUtil Hash = new();
    private readonly Dictionary<string, string?> _writes = new(StringComparer.Ordinal);
    private ILibrarianContainer _container = null!;
    private CancellationToken _token;
    public string Name => "flywheel." + name;
    public IEnumerable<LibrarianWrite> Writes => _writes.Select(p => new LibrarianWrite(Name, p.Key, p.Value));
    public bool Touched { get; private set; }
    private static string Id(TKey key) => Hash.Hash(JsonUtil.Serialize(key)!);

    public void Bind(ILibrarianContainer container, CancellationToken token)
    {
        _container = container;
        _token = token;
        Reset();
    }

    public void Reset() { _writes.Clear(); Touched = false; }

    private async ValueTask<string?> Read(string id)
    {
        Touched = true;
        return _writes.TryGetValue(id, out string? value) ? value : await _container.GetItem(id, _token).NoSync();
    }

    public async ValueTask Set(TKey key, TValue value)
    {
        _token.ThrowIfCancellationRequested();
        string id = Id(key);
        string? order = value is JobRecord job ? SortKey(long.MaxValue - job.CreatedAt, job.Id) : null;
        bool active = value is Schedule { DueAt: not null };
        if (value is Schedule { DueAt: { } due }) order = SortKey(due, key.ToString()!);
        string? scheduled = value is JobRecord { State: var state } pending && state == JobState.Scheduled ? SortKey(pending.DueAt, pending.Id) : null;
        long? completedAt = value is JobRecord completed && (completed.State == JobState.Succeeded || completed.State == JobState.Cancelled || completed.State == JobState.DeadLettered)
            ? completed.CompletedAt : null;
        string serialized = JsonUtil.Serialize(new LibrarianEntry<TKey, TValue>(key, value, order, scheduled, CompletedAt: completedAt, Active: active))!;
        if (await Read(id).NoSync() != serialized) _writes[id] = serialized;

    }

    public async ValueTask<LibrarianEntry<TKey, TValue>?> GetEntry(TKey key)
    {
        string? raw = await Read(Id(key)).NoSync();
        if (raw is null) return null;
        var entry = Decode(raw);
        if (!EqualityComparer<TKey>.Default.Equals(key, entry.Key)) throw new InvalidDataException("Librarian document key mismatch.");
        return entry;
    }

    private static LibrarianEntry<TKey, TValue> Decode(string raw) =>
        JsonUtil.Deserialize<LibrarianEntry<TKey, TValue>>(raw) ?? throw new InvalidDataException("Invalid Flywheel document.");

    public async ValueTask<TValue?> Get(TKey key) => await GetEntry(key).NoSync() is { } entry ? entry.Value : default;
    public async ValueTask<bool> ContainsKey(TKey key) => await Read(Id(key)).NoSync() is not null;

    public async ValueTask<bool> TryAdd(TKey key, TValue value)
    {
        if (await ContainsKey(key).NoSync()) return false;
        await Set(key, value).NoSync();
        return true;
    }

    public async ValueTask<bool> Remove(TKey key)
    {
        string id = Id(key);
        if (await Read(id).NoSync() is null) return false;
        _writes[id] = null;
        return true;
    }

    public async ValueTask<List<TValue>> Find(string field, object? value) =>
        (await FindEntries("value." + field, value).NoSync()).Select(e => e.Value).ToList();

    public async ValueTask<List<LibrarianEntry<TKey, TValue>>> FindEntries(string path, object? value)
    {
        Touched = true;
        await _container.EnsureIndex(path, _token).NoSync();
        var page = await _container.FindByIndex<LibrarianEntry<TKey, TValue>>(path, value, take: int.MaxValue,
            cancellationToken: _token).NoSync();
        var result = new List<LibrarianEntry<TKey, TValue>>();
        // Apply only this transaction's overlay to indexed results.
        foreach (var entry in page.Items)
            if (!_writes.ContainsKey(Id(entry.Key))) result.Add(entry);
        using var expected = System.Text.Json.JsonDocument.Parse(JsonUtil.Serialize(value)!);
        foreach (string? raw in _writes.Values)
            if (raw is not null)
            {
                using var json = System.Text.Json.JsonDocument.Parse(raw);
                var element = json.RootElement;
                foreach (string part in path.Split('.')) element = element.GetProperty(part);
                if (System.Text.Json.JsonElement.DeepEquals(element, expected.RootElement)) result.Add(Decode(raw));
            }
        return result;
    }

    public async ValueTask<List<TKey>> GetKeys() => (await GetAll().NoSync()).Select(p => p.Key).ToList();
    public async ValueTask<List<TValue>> GetValues() => (await GetAll().NoSync()).Select(p => p.Value).ToList();
    public async ValueTask<List<KeyValuePair<TKey, TValue>>> GetAll()
    {
        Touched = true;
        var result = new List<KeyValuePair<TKey, TValue>>();
        foreach (string raw in await _container.GetAllItems(_token).NoSync())
        {
            var entry = Decode(raw);
            if (!_writes.ContainsKey(Id(entry.Key))) result.Add(new KeyValuePair<TKey, TValue>(entry.Key, entry.Value));
        }
        foreach (string? raw in _writes.Values.ToArray())
            if (raw is not null)
            {
                var entry = Decode(raw);
                result.Add(new KeyValuePair<TKey, TValue>(entry.Key, entry.Value));
            }
        return result;
    }

    private static string SortKey(long timestamp, string id)
    {
        var builder = new PooledStringBuilder(20 + id.Length);
        try
        {
            Span<char> number = stackalloc char[20];
            timestamp.TryFormat(number, out int length, "D19", System.Globalization.CultureInfo.InvariantCulture);
            builder.Append(number[..length]);
            builder.Append(':');
            builder.Append(id);
            return builder.ToString();
        }
        finally { builder.Dispose(); }
    }

    public async ValueTask<List<LibrarianEntry<TKey, TValue>>> Range(string path, object? minimum = null,
        object? maximum = null, bool descending = false, int skip = 0, int take = int.MaxValue)
    {
        Touched = true;
        await _container.EnsureIndex(path, _token).NoSync();
        // Paged reads cannot include an uncommitted overlay: callers use them before staging writes.
        if (_writes.Count != 0) throw new InvalidOperationException("Range queries must precede writes to the queried table.");
        var page = await _container.FindRangeByIndex<LibrarianEntry<TKey, TValue>>(path, minimum, maximum,
            descending, skip, take, _token).NoSync();
        return page.Items.ToList();
    }

    public ValueTask<int> Count(string field, object? value) => CountEntries("value." + field, value);

    public async ValueTask<int> CountEntries(string path, object? value)
    {
        if (_writes.Count != 0) return (await FindEntries(path, value).NoSync()).Count;
        Touched = true;
        await _container.EnsureIndex(path, _token).NoSync();
        return await _container.CountByIndex(path, value, _token).NoSync();
    }

    public async ValueTask<int> CountAll()
    {
        if (_writes.Count != 0) return (await GetAll().NoSync()).Count;
        Touched = true;
        await _container.EnsureIndex("marker", _token).NoSync();
        return await _container.CountByIndex("marker", true, _token).NoSync();
    }

}
