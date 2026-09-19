using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Memory;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class LibrarianInteropTests
{
    private sealed class Store(ILibrarianDatabase database) : LibrarianJobStore(database);

    public class ContainerProxy : DispatchProxy
    {
        internal ILibrarianContainer Inner = null!;
        internal string Name = null!;
        internal Database Owner = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            string operation = Name + "/" + method!.Name;
            Owner.Calls[operation] = Owner.Calls.GetValueOrDefault(operation) + 1;
            if (method.Name == nameof(ILibrarianContainer.GetItem) && !Owner.Reads.Add((Name, (string)args![0]!)))
                Owner.DuplicateReads++;
            return method.Invoke(Inner, args);
        }
    }

    internal sealed class Database : ILibrarianDatabase
    {
        internal readonly MemoryLibrarianDatabase Inner = new(NullLogger<MemoryLibrarianDatabase>.Instance);
        internal readonly Dictionary<string, int> Calls = new();
        internal readonly HashSet<(string, string)> Reads = new();
        internal int DuplicateReads;
        internal Func<Task>? BeforeCommit;
        public async ValueTask<ILibrarianContainer> GetContainer(string name, CancellationToken cancellationToken = default)
        {
            var proxy = DispatchProxy.Create<ILibrarianContainer, ContainerProxy>();
            var implementation = (ContainerProxy)proxy;
            implementation.Inner = await Inner.GetContainer(name, cancellationToken);
            implementation.Name = name;
            implementation.Owner = this;
            return proxy;
        }
        public async ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
        {
            if (BeforeCommit is { } action) { BeforeCommit = null; await action(); }
            bool result = await Inner.Execute(batch, cancellationToken);
            Reads.Clear();
            return result;
        }
        public ValueTask MarkDirty(string name, CancellationToken cancellationToken = default) => Inner.MarkDirty(name, cancellationToken);
        public ValueTask Save(CancellationToken cancellationToken = default) => Inner.Save(cancellationToken);
        public ValueTask<bool> UnloadContainer(string name, CancellationToken cancellationToken = default) => Inner.UnloadContainer(name, cancellationToken);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    private static EnqueueRequest Request(string name = "work") => new(name, "{}", new JobPolicy(), TimeSpan.Zero);

    [Test]
    public async Task Enqueue_and_claim_read_each_document_at_most_once_per_attempt()
    {
        await using var database = new Database();
        await using var store = new Store(database);
        await store.Enqueue(Request());
        JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1)))!;
        await store.Renew(lease, TimeSpan.FromMinutes(2));
        Check(database.DuplicateReads == 0, "An attempt fetched the same raw document repeatedly.");
    }

    [Test]
    public async Task Recurring_status_uses_one_bulk_lookup_and_live_sampling_uses_index_counts()
    {
        await using var database = new Database();
        await using var store = new Store(database);
        for (int i = 0; i < 20; i++)
        {
            await store.AddRecurring("schedule-" + i, Request(), TimeSpan.FromMinutes(1));
            await store.RunRecurring("schedule-" + i);
        }
        database.Calls.Clear();
        Check((await store.ListRecurring()).Count == 20, "Recurring results changed.");
        Check(database.Calls.GetValueOrDefault("flywheel.jobs/GetItems") == 1 &&
            database.Calls.GetValueOrDefault("flywheel.jobs/GetItem") == 0, "Recurring status performed individual job reads.");
        database.Calls.Clear();
        Check(await store.SampleLiveActivity(default), "Queued jobs were not observed.");
        Check(database.Calls.GetValueOrDefault("flywheel.jobs/CountRangeByIndex") == 1 &&
            database.Calls.GetValueOrDefault("flywheel.jobs/FindByIndex") == 0 &&
            database.Calls.GetValueOrDefault("flywheel.jobs/GetAllItems") == 0, "Sampling materialized job payloads.");
    }

    [Test]
    public async Task Retried_attempt_discards_cached_values_and_preserves_both_writers_history()
    {
        await using var database = new Database();
        await using var store = new Store(database);
        await using var peer = new Store(database.Inner);
        database.BeforeCommit = async () => { await peer.Enqueue(Request("peer")); };
        await store.Enqueue(Request("original"));
        Check((await store.List()).Count == 2, "Retry lost a job.");
        var history = await store.GetHistory(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Check(history.Sum(point => point.Scheduled) == 2, "Retry reused stale history from its rejected attempt.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
