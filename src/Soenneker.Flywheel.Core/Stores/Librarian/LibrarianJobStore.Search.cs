using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    private readonly ConcurrencyLimiter _searchLimit = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = 4, QueueLimit = 128, QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    });

    // Each reader owns its bindings. A slow dashboard scan never acquires the lease-operation gate.
    private async Task<T> SearchSnapshot<T>(CancellationToken cancellationToken,
        Func<LibrarianTable<string, JobSearchDocument>, LibrarianTable<string, string>, long, CancellationToken, Task<T>> read)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        try
        {
            using RateLimitLease admission = await _searchLimit.AcquireAsync(1, deadline.Token);
            if (!admission.IsAcquired)
            {
                FlywheelStorageMetrics.Rejections.Add(1, new KeyValuePair<string, object?>("operation", "Search"));
                throw new TimeoutException("Flywheel search admission queue is full.");
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
            await BeforeOperation(deadline.Token);
            ILibrarianContainer control = await _database.GetContainer(Control, deadline.Token);
            string? format = await control.GetItem("format", deadline.Token);
            if (format is not (null or "4" or "5")) throw new InvalidDataException("Unsupported Flywheel Librarian format.");
            ILibrarianContainer jobs = await _database.GetContainer("flywheel.jobs", deadline.Token);
            string revisionKey = format == "5" ? "searchRevision" : "flywheel.jobs";
            var table = new LibrarianTable<string, JobSearchDocument>("jobs");
            var payloads = new LibrarianTable<string, string>("payloads");
            ILibrarianContainer payloadContainer = await _database.GetContainer(payloads.Name, deadline.Token);
            for (int attempt = 0; attempt < 128; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string? revision = await control.GetItem(revisionKey, deadline.Token);
                table.Bind(jobs, deadline.Token);
                payloads.Bind(payloadContainer, deadline.Token);
                T result;
                try { result = await read(table, payloads, await _clock(deadline.Token), deadline.Token); }
                catch (InvalidDataException) when (!deadline.IsCancellationRequested)
                {
                    if (revision == await control.GetItem(revisionKey, deadline.Token)) throw;
                    continue;
                }
                if (revision == await control.GetItem(revisionKey, deadline.Token)) return result;
                await Task.Delay(Random.Shared.Next(1, 10), deadline.Token);
            }
            throw new TimeoutException("The job search could not obtain a consistent snapshot.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            FlywheelStorageMetrics.Timeouts.Add(1, new KeyValuePair<string, object?>("operation", "Search"));
            throw new TimeoutException("The job search exceeded its operation deadline.", ex);
        }
    }

    private static async IAsyncEnumerable<JobSearchDocument> SearchCandidates(LibrarianTable<string, JobSearchDocument> table,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string cursor = "";
        while (true)
        {
            var page = await table.Range("order", minimum: cursor, skip: cursor.Length == 0 ? 0 : 1, take: 200);
            foreach (var entry in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry.Value;
            }
            if (page.Count < 200) yield break;
            cursor = page[^1].Order ?? throw new InvalidDataException("A job is missing its search ordering key.");
        }
    }
}
