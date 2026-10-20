using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Core.Logging;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.FileSystem;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.MemoryStream.Abstract;
using System.Threading;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Filesystem.Tests;

public sealed class LogArchivePersistenceTests
{
    [Test]
    public async Task PendingArchiveSurvivesRestartAndHistoryCleanup(CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "flywheel-archive-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var clock = new LogArchiveClock();
        ServiceProvider Open()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(new LogArchiveOptions { Enabled = true, RotationInterval = TimeSpan.FromSeconds(1) });
            services.AddFlywheel().AddFilesystem(options =>
            {
                options.FilePath = Path.Combine(directory, "jobs.json");
                options.HistoryRetention = TimeSpan.FromMinutes(5);
            });
            return services.BuildServiceProvider();
        }
        try
        {
            await using (ServiceProvider provider = Open())
            {
                var store = provider.GetRequiredService<FilesystemJobStore>();
                await store.Enqueue(new EnqueueRequest("archive", "{}", new JobPolicy(), TimeSpan.Zero, null), cancellationToken);
                JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
                await store.AppendLogs(lease, [new JobLogMessage("Information", "test", "pending")], cancellationToken);
                await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
            }
            clock.Advance(TimeSpan.FromMinutes(6));
            await using (ServiceProvider provider = Open())
            {
                var store = provider.GetRequiredService<FilesystemJobStore>();
                await store.Maintain(100, cancellationToken);
                LogArchiveClaim? claim = await store.ClaimLogArchive(cancellationToken);
                if (claim?.File.Entries.Single().Log.Message != "pending") throw new Exception("Restart lost the pending archive.");
                await store.CompleteLogArchive(claim, cancellationToken);
            }
            await using (ServiceProvider provider = Open())
            {
                if (await provider.GetRequiredService<FilesystemJobStore>().ClaimLogArchive(cancellationToken) is not null)
                    throw new Exception("Acknowledgment was not persisted.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public async Task LibrarianArchiveSurvivesReopenAndRepeatedSave(CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "flywheel-archive-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddFlywheel().AddFilesystem(options => options.FilePath = Path.Combine(directory, "jobs.json"))
                .AddLogArchive((sp, id, _) => ValueTask.FromResult<ILibrarianDatabase>(
                    new FileSystemLibrarianDatabase(Path.Combine(directory, id + ".json"),
                        sp.GetRequiredService<IFileUtil>(), sp.GetRequiredService<IMemoryStreamUtil>(),
                        sp.GetRequiredService<ILogger<LibrarianJobLogArchive>>())), options => options.Enabled = true);
            await using ServiceProvider provider = services.BuildServiceProvider();
            var archive = provider.GetRequiredService<IJobLogArchive>();
            var file = new LogArchiveFile(Guid.NewGuid().ToString(), 1000,
                [new LogArchiveEntry("job", new JobLogEntry("entry", 1000, 1, "Information", "test", "Unicode 雪 and quotes \""))]);
            await archive.Save(file, cancellationToken);
            await archive.Save(file, cancellationToken);
            await using var reopened = new FileSystemLibrarianDatabase(Path.Combine(directory, file.Id + ".json"),
                provider.GetRequiredService<IFileUtil>(), provider.GetRequiredService<IMemoryStreamUtil>(),
                provider.GetRequiredService<ILogger<LibrarianJobLogArchive>>());
            ILibrarianContainer container = await reopened.GetContainer("logs", cancellationToken);
            string? json = await container.GetItem(file.Id, cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(json!);
            if (document.RootElement.GetProperty("entries")[0].GetProperty("log").GetProperty("message").GetString() != file.Entries[0].Log.Message)
                throw new Exception("Archive did not persist the original message.");
            try
            {
                await archive.Save(file with { CreatedAt = 2000 }, cancellationToken);
                throw new Exception("Archive overwrote an immutable file.");
            }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
