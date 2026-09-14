using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using System.Runtime.CompilerServices;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Utils.Json;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    private readonly ILibrarianDatabase _database;
    private readonly ILibrarianTable[] _tables;
    private readonly Func<CancellationToken, ValueTask<long>> _clock;
    private readonly bool _ownsDatabase;
    private bool _disposed;
    private const string Control = "flywheel.control";
    private bool _notifyServers;
    private string? _lifecycleRevision;

    protected virtual ValueTask BeforeOperation(CancellationToken token) => ValueTask.CompletedTask;

    private async Task<T> Mutate<T>(CancellationToken ct, Func<long, Task<T>> action)
    {
        using (await _gate.Lock(ct).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await BeforeOperation(ct).NoSync();
            ILibrarianContainer control = await _database.GetContainer(Control, ct).NoSync();
            foreach (ILibrarianTable table in _tables)
                table.Bind(await _database.GetContainer(table.Name, ct).NoSync(), ct);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    foreach (ILibrarianTable table in _tables) table.Reset();
                    _notifyServers = false;
                    var controls = (await control.GetLibrarianItems(ct).NoSync()).ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal);
                    _lifecycleRevision = controls.GetValueOrDefault("revision");
                    string? format = controls.GetValueOrDefault("format");
                    if (format is not null && format != "4") throw new InvalidDataException("Unsupported Flywheel Librarian format.");
                    long now = await _clock(ct).NoSync();
                    T result = default!;
                    InvalidDataException? inconsistent = null;
                    try { result = await action(now).NoSync(); }
                    catch (InvalidDataException ex) { inconsistent = ex; }
                    List<LibrarianWrite> writes = _tables.SelectMany(t => t.Writes).ToList();
                    var conditions = _tables.Where(t => t.Touched).Select(t => new LibrarianCondition(Control, t.Name,
                        controls.GetValueOrDefault(t.Name))).ToList();
                    conditions.Add(new LibrarianCondition(Control, "format", format));
                    if (_idle.Touched) conditions.Add(new LibrarianCondition(Control, "revision", _lifecycleRevision));
                    if (inconsistent is not null)
                    {
                        if (await _database.Execute(new LibrarianBatch([], conditions), ct).NoSync())
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inconsistent).Throw();
                        if (attempt >= 127) throw new TimeoutException("Flywheel reads repeatedly conflicted with another worker.");
                        await Task.Delay(Random.Shared.Next(1, 10), ct).NoSync();
                        continue;
                    }
                    if (format is null) writes.Add(new LibrarianWrite(Control, "format", "4"));
                    if (writes.Count != 0)
                    {
                        string next = Guid.NewGuid().ToString("N");
                        foreach (string table in writes.Select(w => w.Container).Distinct().ToArray())
                            if (table != Control) writes.Add(new LibrarianWrite(Control, table, next));
                        JobChange[] changes = Changes(writes);
                        if (changes.Length != 0)
                        {
                            string? previous = controls.GetValueOrDefault("feed");
                            string? previousId = previous is null ? null : JsonUtil.Deserialize<StoreRevision>(previous)!.Revision;
                            writes.Add(new LibrarianWrite(Control, "feed", JsonUtil.Serialize(new StoreRevision(next, previousId, changes))!));
                            conditions.Add(new LibrarianCondition(Control, "feed", previous));
                        }
                        if (writes.Any(w => w.Container is "flywheel.jobs" or "flywheel.policies" or "flywheel.rates" or "flywheel.schedules"))
                            writes.Add(new LibrarianWrite(Control, "revision", next));
                    }
                    // Even read-only results validate the revision: multiple reads are one optimistic snapshot.
                    if (await _database.Execute(new LibrarianBatch(writes, conditions), ct).NoSync())
                        return result;
                    if (attempt >= 127) throw new TimeoutException("Flywheel transaction repeatedly conflicted with another worker.");
                    await Task.Delay(Random.Shared.Next(1, 10), ct).NoSync();
                }
            }
            finally { foreach (ILibrarianTable table in _tables) table.Reset(); }
        }
    }

    private JobChange[] Changes(List<LibrarianWrite> writes)
    {
        var changes = new HashSet<JobChange>();
        foreach (LibrarianWrite write in writes)
        {
            if (write.Container is "flywheel.jobs" or "flywheel.logs")
            {
                if (write.Value is null) { changes.Add(JobChange.Resync); continue; }
                using var json = System.Text.Json.JsonDocument.Parse(write.Value);
                changes.Add(new JobChange(write.Container == "flywheel.jobs" ? "Job" : "Logs", json.RootElement.GetProperty("key").GetString()!));
            }
            else if (write.Container == "flywheel.schedules") changes.Add(new JobChange("Schedules", null));
            else if (write.Container == "flywheel.nodes" && _notifyServers) changes.Add(new JobChange("Servers", null));
        }
        return changes.ToArray();
    }

    public async IAsyncEnumerable<JobChange> Watch([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ILibrarianContainer control;
        string? previous;
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await BeforeOperation(cancellationToken).NoSync();
            control = await _database.GetContainer(Control, cancellationToken).NoSync();
            previous = await control.GetItem("feed", cancellationToken).NoSync();
        }
        yield return JobChange.Resync;
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).NoSync();
            string? current;
            using (await _gate.Lock(cancellationToken).NoSync())
            {
                if (_disposed) yield break;
                current = await control.GetItem("feed", cancellationToken).NoSync();
            }
            if (current == previous) continue;
            StoreRevision? entry = current is null ? null : JsonUtil.Deserialize<StoreRevision>(current);
            string? previousId = previous is null ? null : JsonUtil.Deserialize<StoreRevision>(previous)!.Revision;
            previous = current;
            if (entry is null || entry.Previous != previousId) yield return JobChange.Resync;
            else foreach (JobChange change in entry.Changes) yield return change;
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        using (await _gate.Lock().NoSync())
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsDatabase) await _database.DisposeAsync().NoSync();
        }
    }
}
