using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Stores.Librarian;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Memory;
using System.Threading;

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
    public async ValueTask Enqueue_and_claim_read_each_document_at_most_once_per_attempt(CancellationToken cancellationToken)
    {
        await using var database = new Database();
        await using var store = new Store(database);
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
        await store.Renew(lease, TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
        Check(database.DuplicateReads == 0, "An attempt fetched the same raw document repeatedly.");
    }

    [Test]
    public async ValueTask Recurring_status_uses_one_bulk_lookup_and_live_sampling_uses_index_counts(CancellationToken cancellationToken)
    {
        await using var database = new Database();
        await using var store = new Store(database);
        for (int i = 0; i < 20; i++)
        {
            await store.AddRecurring("schedule-" + i, Request(), TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
            await store.RunRecurring("schedule-" + i, cancellationToken: cancellationToken);
        }
        database.Calls.Clear();
        Check((await store.ListRecurring(cancellationToken: cancellationToken)).Count == 20, "Recurring results changed.");
        Check(database.Calls.GetValueOrDefault("flywheel.jobs/GetItems") == 1 &&
            database.Calls.GetValueOrDefault("flywheel.jobs/GetItem") == 0, "Recurring status performed individual job reads.");
        database.Calls.Clear();
        Check(await store.SampleLiveActivity(cancellationToken), "Queued jobs were not observed.");
        Check(database.Calls.GetValueOrDefault("flywheel.jobs/CountRangeByIndex") == 1 &&
            database.Calls.GetValueOrDefault("flywheel.jobs/FindByIndex") == 0 &&
            database.Calls.GetValueOrDefault("flywheel.jobs/GetAllItems") == 0, "Sampling materialized job payloads.");
    }

    [Test]
    public async ValueTask Retried_attempt_discards_cached_values_and_preserves_both_writers_history(CancellationToken cancellationToken)
    {
        await using var database = new Database();
        await using var store = new Store(database);
        await using var peer = new Store(database.Inner);
        database.BeforeCommit = async () => { await peer.Enqueue(Request("peer"), cancellationToken: cancellationToken); };
        await store.Enqueue(Request("original"), cancellationToken: cancellationToken);
        Check((await store.List(cancellationToken: cancellationToken)).Count == 2, "Retry lost a job.");
        var history = await store.GetHistory(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(1), cancellationToken: cancellationToken);
        Check(history.Sum(point => point.Scheduled) == 2, "Retry reused stale history from its rejected attempt.");
    }

    [Test]
    public async Task Null_stored_pools_preserve_default_rate_limits_and_allow_named_pools(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        await using var store = new Store(database);
        var policy = new MethodPolicy { RateLimit = 1, RateWindow = TimeSpan.FromHours(1) };
        await store.ConfigureMethod("job", policy, cancellationToken);
        await store.Enqueue(Request("job"), cancellationToken: cancellationToken);
        await store.Enqueue(Request("job"), cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken))!;
        await store.Finish(lease, Soenneker.Flywheel.Communication.Enums.JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken);

        string id = new Soenneker.Hashing.Sha256.Sha256HashingUtil().Hash("\"job\"");
        var container = await database.GetContainer("flywheel.policies", cancellationToken);
        var document = System.Text.Json.Nodes.JsonNode.Parse((await container.GetItem(id, cancellationToken))!)!;
        document["value"]!["pools"] = null;
        Check(await database.Execute(new LibrarianBatch([new LibrarianWrite("flywheel.policies", id, document.ToJsonString())]), cancellationToken), "Could not seed null pools.");

        await store.ConfigureMethod("job", policy, cancellationToken);
        Check(await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken) is null, "Equivalent default registration reset the rate limit.");
        await store.ConfigureMethod("job", policy with { Pools = ["named"] }, cancellationToken);
        Check(await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken) is null, "Default worker claimed named-pool work.");
        Check(await store.ClaimForWorker("worker", TimeSpan.FromMinutes(1), "v1", new HashSet<string> { "job" }, "named", cancellationToken) is not null, "Named-pool policy was not applied.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
