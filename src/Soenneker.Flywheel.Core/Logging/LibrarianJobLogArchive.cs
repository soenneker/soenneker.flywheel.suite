using System.Text.Json;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Flywheel.Core.Logging;

/// <summary>Archives through a dedicated Librarian database per file.
/// The factory must return a fresh owned database targeting a stable address for the supplied ID.
/// Use filesystem or blob snapshot providers; provider envelopes can exceed the file JSON size.</summary>
public sealed class LibrarianJobLogArchive(Func<string, CancellationToken, ValueTask<ILibrarianDatabase>> databaseFactory) : IJobLogArchive
{
    public async Task Save(LogArchiveFile file, CancellationToken cancellationToken = default)
    {
        await using ILibrarianDatabase database = await databaseFactory(file.Id, cancellationToken);
        ILibrarianContainer container = await database.GetContainer("logs", cancellationToken);
        string json = JsonSerializer.Serialize(file, LogArchiveJsonContext.Default.LogArchiveFile);
        string? existing = await container.GetItem(file.Id, cancellationToken);
        if (existing is null)
            await container.AddItem(file.Id, json, cancellationToken);
        else if (existing != json)
            throw new InvalidDataException("An archive file ID already contains different data.");
        await database.Save(cancellationToken);
    }
}
