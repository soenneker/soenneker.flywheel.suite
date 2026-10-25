using System.Runtime.CompilerServices;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    // Each reader owns its bindings. A slow dashboard scan never acquires the lease-operation gate.
    private async Task<T> SearchSnapshot<T>(CancellationToken cancellationToken,
        Func<LibrarianTable<string, JobSearchDocument>, long, CancellationToken, Task<T>> read)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await BeforeOperation(deadline.Token);
            ILibrarianContainer control = await _database.GetContainer(Control, deadline.Token);
            string? format = await control.GetItem("format", deadline.Token);
            if (format is not null && format != "4") throw new InvalidDataException("Unsupported Flywheel Librarian format.");
            ILibrarianContainer jobs = await _database.GetContainer(_jobs.Name, deadline.Token);
            var table = new LibrarianTable<string, JobSearchDocument>("jobs");
            for (int attempt = 0; attempt < 128; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string? revision = await control.GetItem(_jobs.Name, deadline.Token);
                table.Bind(jobs, deadline.Token);
                T result = await read(table, await _clock(deadline.Token), deadline.Token);
                if (revision == await control.GetItem(_jobs.Name, deadline.Token)) return result;
                await Task.Delay(Random.Shared.Next(1, 10), deadline.Token);
            }
            throw new TimeoutException("The job search could not obtain a consistent snapshot.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The job search exceeded its operation deadline.", ex);
        }
    }

    private static async IAsyncEnumerable<JobRecord> SearchCandidates(LibrarianTable<string, JobSearchDocument> table,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string cursor = "";
        while (true)
        {
            var page = await table.Range("order", minimum: cursor, skip: cursor.Length == 0 ? 0 : 1, take: 200);
            foreach (var entry in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entry.Value.ToRecord();
            }
            if (page.Count < 200) yield break;
            cursor = page[^1].Order ?? throw new InvalidDataException("A job is missing its search ordering key.");
        }
    }
}
