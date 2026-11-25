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
    private ILibrarianTable[] _tables => Session.Tables;
    private readonly string[] _controlIds;
    private readonly Func<CancellationToken, ValueTask<long>> _clock;
    private readonly bool _ownsDatabase;
    private readonly TimeSpan _operationTimeout;
    private volatile bool _disposed;
    private readonly SharedJobChangeFeed _changeFeed;
    private static readonly string[] FeedKeys = Enumerable.Range(0, 16).Select(i => $"feed-{i}").ToArray();
    private static readonly string[] FeedSnapshotKeys = [.. FeedKeys, "feed"];
    private const string Control = "flywheel.control";

    protected virtual ValueTask BeforeOperation(CancellationToken token) => ValueTask.CompletedTask;

    private Task<T> ReadSnapshot<T>(CancellationToken token, Func<long, Task<T>> action, bool pointReadsOnly = false, [CallerMemberName] string operation = "") =>
        Mutate(token, action, lane: _readers, pointReadsOnly: pointReadsOnly, operation: operation);

    private async Task<T> Mutate<T>(CancellationToken cancellationToken, Func<long, Task<T>> action, bool publishChanges = true, StoreLane? lane = null, bool pointReadsOnly = false,
        [CallerMemberName] string operation = "")
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        lane ??= _normal;
        if (Interlocked.Increment(ref lane.Pending) > 1024)
        {
            Interlocked.Decrement(ref lane.Pending);
            FlywheelStorageMetrics.Rejections.Add(1, new KeyValuePair<string, object?>("operation", operation));
            throw new TimeoutException("Flywheel storage admission queue is full.");
        }
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                long waiting = System.Diagnostics.Stopwatch.GetTimestamp();
                using (await lane.Gate.Lock(deadline.Token).NoSync())
                {
                    _session.Value = lane.State;
                    FlywheelStorageMetrics.GateWait.Record(System.Diagnostics.Stopwatch.GetElapsedTime(waiting).TotalMilliseconds,
                        new KeyValuePair<string, object?>("operation", operation));
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    (bool committed, T result) = await TryMutate(deadline.Token, action, publishChanges, pointReadsOnly).NoSync();
                    FlywheelStorageMetrics.Duration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        new KeyValuePair<string, object?>("operation", operation));
                    if (committed) return result;
                    FlywheelStorageMetrics.Conflicts.Add(1, new KeyValuePair<string, object?>("operation", operation));
                }
                if (attempt >= 127) throw new TimeoutException("Flywheel transaction repeatedly conflicted with another worker.");
                // Release the shared table state and gate so renewals can progress; retries share the operation deadline.
                await Task.Delay(Random.Shared.Next(1, 10), deadline.Token).NoSync();
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            FlywheelStorageMetrics.Timeouts.Add(1, new KeyValuePair<string, object?>("operation", operation));
            throw new TimeoutException("Flywheel storage exceeded its operation deadline.", ex);
        }
        finally { Interlocked.Decrement(ref lane.Pending); }
    }

    private async Task<(bool Committed, T Result)> TryMutate<T>(CancellationToken ct, Func<long, Task<T>> action, bool publishChanges, bool pointReadsOnly)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await BeforeOperation(ct).NoSync();
        ILibrarianContainer control = await _database.GetContainer(Control, ct).NoSync();
        foreach (ILibrarianTable table in _tables)
            table.Bind(await _database.GetContainer(table.Name, ct).NoSync(), ct);
        try
        {
            Session.NotifyServers = false;
            string?[]? controlValues = pointReadsOnly ? null : await control.GetItems(_controlIds, ct).NoSync();
            Session.LifecycleRevision = controlValues?[1];
            string? format = controlValues is null ? await control.GetItem("format", ct).NoSync() : controlValues[0];
            if (format is not (null or "4" or "5")) throw new InvalidDataException("Unsupported Flywheel Librarian format.");
            long now = await _clock(ct).NoSync();
            T result = default!;
            InvalidDataException? inconsistent = null;
            try { result = await action(now).NoSync(); }
            catch (InvalidDataException ex) { inconsistent = ex; }
            int writeCount = 0, touchedCount = 0;
            foreach (ILibrarianTable table in _tables)
            {
                writeCount += table.WriteCount;
                if (table.Touched) touchedCount++;
            }
            var writes = new List<LibrarianWrite>(writeCount + (writeCount == 0 ? 0 : touchedCount + 3));
            var conditions = new List<LibrarianCondition>(touchedCount + 3);
            for (int i = 0; i < _tables.Length; i++)
            {
                ILibrarianTable table = _tables[i];
                table.AddWrites(writes);
                table.AddConditions(conditions, Control, controlValues, i + 2);
            }
            conditions.Add(new LibrarianCondition(Control, "format", format));
            if (_idle.Touched) conditions.Add(new LibrarianCondition(Control, "revision", Session.LifecycleRevision));
            if (inconsistent is not null)
            {
                if (await _database.Execute(new LibrarianBatch([], conditions), ct).NoSync())
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inconsistent).Throw();
                return (false, default!);
            }
            if (format != "5" && writeCount != 0) writes.Add(new LibrarianWrite(Control, "format", "5"));
            if (writes.Count != 0)
            {
                string next = Guid.NewGuid().ToString("N");
                foreach (ILibrarianTable table in _tables)
                    if (table.WriteCount != 0) writes.Add(new LibrarianWrite(Control, table.Name, next));
                // Renewals update authoritative ownership without serializing on the shared notification feed.
                // They cannot make queued work eligible; dashboards read current lease details on demand.
                JobChange[] changes = publishChanges ? Changes() : [];
                if (changes.Length != 0)
                {
                    string feedKey = FeedKeys[Shard(changes[0].JobId ?? changes[0].Kind)];
                    string? previous = await control.GetItem(feedKey, ct).NoSync();
                    string? previousId = previous is null ? null : JsonUtil.Deserialize<StoreRevision>(previous, LibraryJsonContext.Get<StoreRevision>())!.Revision;
                    writes.Add(new LibrarianWrite(Control, feedKey, JsonUtil.Serialize(new StoreRevision(next, previousId, changes), LibraryJsonContext.Get<StoreRevision>())!));
                    // One unconditional tip lets every idle watcher poll a single key.
                    writes.Add(new LibrarianWrite(Control, "feed", next));
                    conditions.Add(new LibrarianCondition(Control, feedKey, previous));
                }
                if (publishChanges && _jobs.WriteCount != 0) writes.Add(new LibrarianWrite(Control, "searchRevision", next));
                if (publishChanges && (_jobs.WriteCount != 0 || _policies.WriteCount != 0 || _rates.WriteCount != 0 || _schedules.WriteCount != 0))
                    writes.Add(new LibrarianWrite(Control, "revision", next));
            }
            // Even read-only results validate the revision: multiple reads are one optimistic snapshot.
            if (await _database.Execute(new LibrarianBatch(writes, conditions), ct).NoSync())
                return (true, result);
            return (false, default!);
        }
        finally { foreach (ILibrarianTable table in _tables) table.Reset(); }
    }
    private JobChange[] Changes()
    {
        bool schedules = _schedules.WriteCount != 0;
        bool servers = _nodes.WriteCount != 0 && Session.NotifyServers;
        if (_jobs.WriteCount == 0 && _logs.WriteCount == 0 && !schedules && !servers) return [];
        var changes = new HashSet<JobChange>();
        if (_jobs.WriteCount != 0) _jobs.AddJobChanges(changes, "Job");
        if (_logs.WriteCount != 0) _logs.AddJobChanges(changes, "Logs");
        if (schedules) changes.Add(new JobChange("Schedules"));
        if (servers) changes.Add(new JobChange("Servers"));
        return changes.ToArray();
    }

    public IAsyncEnumerable<JobChange> Watch(CancellationToken cancellationToken = default) => _changeFeed.Watch(cancellationToken);

    private async IAsyncEnumerable<JobChange> PollChanges([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ILibrarianContainer control;
        string?[] previous;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(_operationTimeout);
            using (await _normal.Gate.Lock(deadline.Token).NoSync())
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await BeforeOperation(deadline.Token).NoSync();
                control = await _database.GetContainer(Control, deadline.Token).NoSync();
            }
            previous = await control.GetItems(FeedSnapshotKeys, deadline.Token).NoSync();
        }
        yield return JobChange.Resync;
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).NoSync();
            if (_disposed) yield break;
            // The feed has no shared table bindings; a stalled subscription must not block lease renewal.
            using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            read.CancelAfter(_operationTimeout);
            if (await control.GetItem("feed", read.Token).NoSync() == previous[^1]) continue;
            string?[] current = await control.GetItems(FeedSnapshotKeys, read.Token).NoSync();
            int changed = -1;
            for (int i = 0; i < FeedKeys.Length; i++)
            {
                if (previous[i] == current[i]) continue;
                if (changed >= 0) { changed = -2; break; }
                changed = i;
            }
            string? before = changed >= 0 ? previous[changed] : null;
            string? after = changed >= 0 ? current[changed] : null;
            previous = current;
            StoreRevision? entry = after is null ? null : JsonUtil.Deserialize<StoreRevision>(after, LibraryJsonContext.Get<StoreRevision>());
            string? previousId = before is null ? null : JsonUtil.Deserialize<StoreRevision>(before, LibraryJsonContext.Get<StoreRevision>())!.Revision;
            if (entry is null || entry.Previous != previousId) yield return JobChange.Resync;
            else foreach (JobChange change in entry.Changes) yield return change;
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        await _changeFeed.DisposeAsync();
        using (await _normal.Gate.Lock().NoSync())
        using (await _completions.Gate.Lock().NoSync())
        using (await _renewals.Gate.Lock().NoSync())
        using (await _heartbeats.Gate.Lock().NoSync())
        using (await _readers.Gate.Lock().NoSync())
        {
            if (_disposed) return;
            _searchLimit.Dispose();
            if (_ownsDatabase) await _database.DisposeAsync().NoSync();
            _disposed = true;
        }
    }
}
