using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Logging;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.FileSystem;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.MemoryStream.Abstract;
using System.Threading;

namespace Soenneker.Flywheel.Filesystem.Tests;

public sealed class LogArchiveRecurringJobTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RecurringExportUsesNormalExecutionAndRecordsStorageFailures(bool failStorage, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), "flywheel-archive-recurring", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var clock = new LogArchiveClock();
        ServiceProvider Open(bool enabled)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<Soenneker.Flywheel.Core.Dashboard.DashboardSnapshotFactory>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddFlywheel().AddFilesystem(o => o.FilePath = Path.Combine(directory, "jobs.json"))
                .AddLogArchive((sp, id, _) => ValueTask.FromResult<ILibrarianDatabase>(
                    new FileSystemLibrarianDatabase(failStorage ? directory : Path.Combine(directory, id + ".json"),
                        sp.GetRequiredService<IFileUtil>(), sp.GetRequiredService<IMemoryStreamUtil>(),
                        sp.GetRequiredService<ILogger<LibrarianJobLogArchive>>())), options =>
                {
                    options.Enabled = enabled;
                    options.RotationInterval = TimeSpan.FromSeconds(1);
                });
            return services.BuildServiceProvider();
        }
        async Task Register(ServiceProvider provider)
        {
            // Start registration services only; drive the real executor explicitly instead of starting worker loops.
            foreach (IHostedService service in provider.GetServices<IHostedService>().Where(s => s is not BackgroundService))
                await service.StartAsync(cancellationToken);
        }
        try
        {
            await using (ServiceProvider provider = Open(true))
            {
                await Register(provider);
                await Register(provider);
                var store = provider.GetRequiredService<FilesystemJobStore>();
                var schedule = (await store.ListRecurring(cancellationToken: cancellationToken)).Single();
                if (schedule.Name != "Flywheel.LogArchive.Export" || !schedule.IsSystem) throw new Exception("Archive schedule is not visible.");
                await store.Enqueue(new EnqueueRequest("sample", "{}", new JobPolicy(), TimeSpan.Zero), cancellationToken);
                JobLease lease = (await store.Claim("seed", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
                await store.AppendLogs(lease, [new JobLogMessage("Information", "test", "archive me")], cancellationToken);
                await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
                clock.Advance(TimeSpan.FromSeconds(2));
                string executionId = (await store.RunRecurring(schedule.Id, cancellationToken))!;
                if (!await provider.GetRequiredService<IJobExecutor>().RunOnce(cancellationToken))
                    throw new Exception("Recurring export was not executable.");
                var execution = (await store.Get(executionId, cancellationToken))!;
                var snapshots = provider.GetRequiredService<Soenneker.Flywheel.Core.Dashboard.DashboardSnapshotFactory>();
                if (!execution.IsSystem || !snapshots.Job(execution).IsSystem ||
                    !snapshots.Schedules([schedule], []).Recurring.Single().IsSystem || lease.Job.IsSystem)
                    throw new Exception("System designation was lost or applied to an application job.");
                if (execution.State != (failStorage ? JobState.DeadLettered : JobState.Succeeded))
                    throw new Exception("Dashboard execution did not record the archive outcome.");
                if (failStorage && string.IsNullOrWhiteSpace(execution.Error))
                    throw new Exception("Archive failure was swallowed.");
                clock.Advance(TimeSpan.FromMinutes(2));
                LogArchiveClaim? pending = await store.ClaimLogArchive(cancellationToken);
                if ((pending?.File.Entries.Any(e => e.Log.Message == "archive me") == true) != failStorage)
                    throw new Exception("Archive retry or acknowledgment was incorrect.");
            }
            await using (ServiceProvider provider = Open(false))
            {
                await Register(provider);
                if ((await provider.GetRequiredService<FilesystemJobStore>().ListRecurring(cancellationToken: cancellationToken)).Count != 0)
                    throw new Exception("Disabled archive left its recurring schedule active.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
