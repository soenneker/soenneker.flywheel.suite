using Soenneker.Extensions.ValueTask;
using Soenneker.Hashing.Sha256;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Librarian.Abstractions.Queries;
using System.Linq.Expressions;
using Soenneker.Utils.Json;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using System.Text.Json;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

// Raw reads and writes belong to one optimistic attempt and are discarded before every retry.
internal sealed class LibrarianTable<TKey, TValue>(string name) : ILibrarianTable
    where TKey : notnull
{
    static LibrarianTable() => LibrarianJson.Register(LibraryJsonContext.Get<LibrarianEntry<TKey, TValue>>());

    private static readonly Sha256HashingUtil Hash = new();
    private readonly Dictionary<string, string?> _writes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _reads = new(StringComparer.Ordinal);
    private readonly Dictionary<TKey, string> _ids = new();
    private ILibrarianContainer _container = null!;
    private CancellationToken _token;
    public string Name { get; } = "flywheel." + name;
    public int WriteCount => _writes.Count;
    public void AddWrites(List<LibrarianWrite> writes)
    {
        foreach ((string id, string? value) in _writes) writes.Add(new LibrarianWrite(Name, id, value));
    }
    public bool Touched { get; private set; }

    public IQueryable<LibrarianEntry<TKey, TValue>> Query()
    {
        if (_writes.Count != 0) throw new InvalidOperationException("Queries must precede writes to the queried table.");
        Touched = true;
        return _container.BuildQueryable<LibrarianEntry<TKey, TValue>>();
    }

    public ValueTask<LibrarianEntry<TKey, TValue>?> First(IQueryable<LibrarianEntry<TKey, TValue>> query) =>
        query.FirstOrDefaultAsync(_token);

    public ValueTask<int> Count(Expression<Func<LibrarianEntry<TKey, TValue>, bool>> predicate) =>
        Query().CountAsync(predicate, _token);
    private string Id(TKey key)
    {
        if (_ids.TryGetValue(key, out string? id)) return id;
        id = Hash.Hash(JsonUtil.Serialize(key, LibraryJsonContext.Get<TKey>())!);
        _ids.Add(key, id);
        return id;
    }

    public void Bind(ILibrarianContainer container, CancellationToken token)
    {
        _container = container;
        _token = token;
        Reset();
    }

    public void Reset() { _writes.Clear(); _reads.Clear(); _ids.Clear(); Touched = false; }

    private async ValueTask<string?> Read(string id)
    {
        Touched = true;
        _token.ThrowIfCancellationRequested();
        if (_writes.TryGetValue(id, out string? value) || _reads.TryGetValue(id, out value)) return value;
        value = await _container.GetItem(id, _token).NoSync();
        _reads.Add(id, value);
        return value;
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
        string serialized = JsonUtil.Serialize(new LibrarianEntry<TKey, TValue>(key, value, order, scheduled, CompletedAt: completedAt, Active: active), LibraryJsonContext.Get<LibrarianEntry<TKey, TValue>>())!;
        if (await Read(id).NoSync() != serialized)
        {
            if (_reads.TryGetValue(id, out string? original) && original == serialized) _writes.Remove(id);
            else _writes[id] = serialized;
        }

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
        JsonUtil.Deserialize<LibrarianEntry<TKey, TValue>>(raw, LibraryJsonContext.Get<LibrarianEntry<TKey, TValue>>()) ?? throw new InvalidDataException("Invalid Flywheel document.");

    public async ValueTask<TValue?> Get(TKey key) => await GetEntry(key).NoSync() is { } entry ? entry.Value : default;

    public async ValueTask<TProjection?[]> GetMany<TProjection>(IReadOnlyList<TKey> keys)
    {
        if (keys.Count == 0) return [];
        Touched = true;
        var ids = new string[keys.Count];
        var missing = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Count; i++)
        {
            ids[i] = Id(keys[i]);
            if (!_writes.ContainsKey(ids[i]) && !_reads.ContainsKey(ids[i])) missing.Add(ids[i]);
        }
        if (missing.Count > 0)
        {
            string[] requested = missing.ToArray();
            string?[] raw = await _container.GetItems(requested, _token).NoSync();
            for (int i = 0; i < requested.Length; i++) _reads.Add(requested[i], raw[i]);
        }
        var result = new TProjection?[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            _token.ThrowIfCancellationRequested();
            string? raw = _writes.TryGetValue(ids[i], out string? staged) ? staged : _reads[ids[i]];
            if (raw is null) continue;
            var entry = JsonUtil.Deserialize(raw, LibraryJsonContext.Get<LibrarianEntry<TKey, TProjection>>())
                ?? throw new InvalidDataException("Invalid Flywheel document.");
            if (!EqualityComparer<TKey>.Default.Equals(keys[i], entry.Key)) throw new InvalidDataException("Librarian document key mismatch.");
            result[i] = entry.Value;
        }
        return result;
    }
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

    public async ValueTask<List<TValue>> Find(string field, object? value)
    {
        var entries = await FindEntries("value." + field, value).NoSync();
        var result = new List<TValue>(entries.Count);
        for (int i = 0; i < entries.Count; i++) result.Add(entries[i].Value);
        return result;
    }

    public async ValueTask<bool> Any(string field, string value)
    {
        Touched = true;
        string path = "value." + field;
        await _container.EnsureIndex(path, _token).NoSync();
        // Staged writes can hide at most this many persisted matches. One extra match proves existence.
        var page = await _container.FindByIndex<LibrarianEntry<TKey, TValue>>(path, value, take: _writes.Count + 1,
            cancellationToken: _token).NoSync();
        if (page.Items.Any(entry => !_writes.ContainsKey(Id(entry.Key)))) return true;
        foreach (string? raw in _writes.Values)
        {
            if (raw is null) continue;
            using var json = JsonDocument.Parse(raw);
            if (json.RootElement.GetProperty("value").TryGetProperty(field, out var element) &&
                element.ValueKind == JsonValueKind.String && element.GetString() == value) return true;
        }
        return false;
    }

    public async ValueTask<IReadOnlyList<LibrarianEntry<TKey, TValue>>> FindEntries(string path, object? value)
    {
        Touched = true;
        await _container.EnsureIndex(path, _token).NoSync();
        // Keep indexed reads on the same serialization contract as raw reads and writes.
        var page = await _container.FindByIndex<LibrarianEntry<TKey, TValue>>(path, value, take: int.MaxValue,
            cancellationToken: _token).NoSync();
        if (_writes.Count == 0) return page.Items;
        var result = new List<LibrarianEntry<TKey, TValue>>(page.Items.Count + _writes.Count);
        // Apply only this transaction's overlay to indexed results.
        foreach (var entry in page.Items)
        {
            if (!_writes.ContainsKey(Id(entry.Key))) result.Add(entry);
        }
        using var expected = System.Text.Json.JsonDocument.Parse(JsonUtil.Serialize(value, LibraryJsonContext.Get<object?>())!);
        string[] segments = path.Split('.');
        foreach (string? raw in _writes.Values)
            if (raw is not null)
            {
                using var json = System.Text.Json.JsonDocument.Parse(raw);
                var element = json.RootElement;
                foreach (string part in segments) element = element.GetProperty(part);
                if (System.Text.Json.JsonElement.DeepEquals(element, expected.RootElement)) result.Add(Decode(raw));
            }
        return result;
    }

    public async ValueTask<List<TKey>> GetKeys() => (await GetAll().NoSync()).Select(p => p.Key).ToList();
    public async ValueTask<List<TValue>> GetValues()
    {
        Touched = true;
        var items = await _container.GetAllItems(_token).NoSync();
        var result = new List<TValue>(items.Count + _writes.Count);
        foreach (string raw in items)
        {
            var entry = Decode(raw);
            if (_writes.Count == 0 || !_writes.ContainsKey(Id(entry.Key))) result.Add(entry.Value);
        }
        foreach (string? raw in _writes.Values)
            if (raw is not null) result.Add(Decode(raw).Value);
        return result;
    }
    public async ValueTask<List<KeyValuePair<TKey, TValue>>> GetAll()
    {
        Touched = true;
        var result = new List<KeyValuePair<TKey, TValue>>();
        var items = await _container.GetAllItems(_token).NoSync();
        result.EnsureCapacity(items.Count + _writes.Count);
        foreach (string raw in items)
        {
            var entry = Decode(raw);
            if (_writes.Count == 0 || !_writes.ContainsKey(Id(entry.Key))) result.Add(new KeyValuePair<TKey, TValue>(entry.Key, entry.Value));
        }
        foreach (string? raw in _writes.Values)
            if (raw is not null)
            {
                var entry = Decode(raw);
                result.Add(new KeyValuePair<TKey, TValue>(entry.Key, entry.Value));
            }
        return result;
    }

    private static string SortKey(long timestamp, string id)
    {
        int numberLength = timestamp < 0 ? 20 : 19;
        return string.Create(numberLength + 1 + id.Length, (timestamp, id, numberLength), static (destination, state) =>
        {
            state.timestamp.TryFormat(destination, out _, "D19", System.Globalization.CultureInfo.InvariantCulture);
            destination[state.numberLength] = ':';
            state.id.AsSpan().CopyTo(destination[(state.numberLength + 1)..]);
        });
    }

    public ValueTask<IReadOnlyList<LibrarianEntry<TKey, TValue>>> Range(string path, object? minimum = null,
        object? maximum = null, bool descending = false, int skip = 0, int take = int.MaxValue) =>
        Range<TValue>(path, minimum, maximum, descending, skip, take);

    public async ValueTask<IReadOnlyList<LibrarianEntry<TKey, TProjection>>> Range<TProjection>(string path, object? minimum = null,
        object? maximum = null, bool descending = false, int skip = 0, int take = int.MaxValue)
    {
        Touched = true;
        await _container.EnsureIndex(path, _token).NoSync();
        // Paged reads cannot include an uncommitted overlay: callers use them before staging writes.
        if (_writes.Count != 0) throw new InvalidOperationException("Range queries must precede writes to the queried table.");
        LibrarianJson.Register(LibraryJsonContext.Get<LibrarianEntry<TKey, TProjection>>());
        var page = await _container.FindRangeByIndex<LibrarianEntry<TKey, TProjection>>(path, minimum, maximum,
            descending, skip, take, _token).NoSync();
        return page.Items;
    }

    public async ValueTask<int> CountRange(string path, object? minimum = null, object? maximum = null)
    {
        if (_writes.Count != 0) throw new InvalidOperationException("Range counts must precede writes to the queried table.");
        Touched = true;
        await _container.EnsureIndex(path, _token).NoSync();
        return await _container.CountRangeByIndex(path, minimum, maximum, _token).NoSync();
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
        Touched = true;
        int count = await _container.CountItems(_token).NoSync();
        foreach ((string id, string? value) in _writes)
        {
            // Set and Remove capture the original raw value before staging a change.
            if (_reads[id] is null && value is not null) count++;
            else if (_reads[id] is not null && value is null) count--;
        }
        return count;
    }

}
