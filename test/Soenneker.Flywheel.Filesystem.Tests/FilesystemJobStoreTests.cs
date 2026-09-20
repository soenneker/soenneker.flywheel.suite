using System.Reflection;
using Microsoft.Extensions.Logging;
using Soenneker.Utils.MemoryStream.Abstract;
using Soenneker.Librarian.FileSystem;
using Soenneker.Utils.File.Abstract;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Filesystem.Tests;

public sealed class FilesystemJobStoreTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }

    private sealed class Files : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flywheel-filesystem-tests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "jobs.json");
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }

    private static ServiceProvider Open(string path, Clock? clock = null, bool retain = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (clock is not null) services.AddSingleton<TimeProvider>(clock);
        services.AddFlywheel().AddFilesystem(o => { o.FilePath = path; o.RetainCompletedJobs = retain; });
        return services.BuildServiceProvider();
    }

    private static EnqueueRequest Request(string name = "job", string? key = null) => new(name, "{}", new JobPolicy(), TimeSpan.Zero, key);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public async Task ServerWorkerHistorySurvivesReopening()
    {
        using var files = new Files();
        var clock = new Clock();
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            await store.Enqueue(Request());
            await store.Claim("node", TimeSpan.FromMinutes(1));
            await store.Heartbeat("node", 4, TimeSpan.FromMinutes(1));
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var server = await services.GetRequiredService<FilesystemJobStore>().GetServer("node");
            Check(server is not null && server.WorkerHistory.Single().BusyWorkers == 1,
                "Stored worker history must survive reopening without a dashboard session.");
        }
    }
    [Test]
    public async Task JobsLeasesLogsProgressAndLimitsSurviveReopening()
    {
        using var files = new Files();
        var clock = new Clock();
        JobLease lease;
        string id;
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            await store.ConfigureMethod("job", new MethodPolicy { MaxConcurrency = 1, RateLimit = 1, RateWindow = TimeSpan.FromMinutes(1) });
            id = await store.Enqueue(Request(key: "key"));
            lease = (await store.Claim("node", TimeSpan.FromSeconds(30)))!;
            await store.SetProgress(lease, 25, "progress");
            await store.AppendLogs(lease, [new JobLogMessage("Info", "test", "persist me")]);
            await store.Heartbeat("node", 2, TimeSpan.FromMinutes(1));
            Check(await services.GetRequiredService<IFileUtil>().Exists(files.Path) && (await File.ReadAllTextAsync(files.Path)).Contains(id), "Enqueue was not persisted before returning.");
        }
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            JobRecord job = (await store.Get(id))!;
            Check(job.State == JobState.Running && job.Progress == 25 && job.Token == lease.Token, "Lease or progress did not round trip.");
            Check((await store.GetLogs(id)).Single().Message == "persist me", "Log did not round trip.");
            Check(await store.GetTotalWorkerCount() == 2, "Heartbeat did not round trip.");
            Check(await store.Enqueue(Request(key: "key")) == id, "Dedupe lost across reopen.");
            await store.Enqueue(Request());
            Check(await store.Claim("node2", TimeSpan.FromMinutes(1)) is null, "Concurrency policy lost across reopen.");
            Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Persisted lease could not finish.");
            Check(await store.Claim("node2", TimeSpan.FromMinutes(1)) is null, "Rate usage lost across reopen.");
            Check((await store.GetSearchHistory(null, null, null)).Sum(p => p.Succeeded) == 1, "Search history lost.");
        }
    }

    [Test]
    public async Task ChainsSchedulesAndVersionMarkersSurviveReopening()
    {
        using var files = new Files();
        var clock = new Clock();
        IReadOnlyList<string> ids;
        string version;
        await using (ServiceProvider services = Open(files.Path, clock, retain: false))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            ids = await store.EnqueueChain([Request("first"), Request("second")], "chain");
            version = await store.EnqueueForCurrentInstance(Request("version"), "v1", "node");
            await store.AddRecurring("interval", Request("recurring"), TimeSpan.FromMinutes(1));
            await store.AddCron("cron", Request("cron"), "*/10 * * * * *", includeSeconds: true);
        }
        await using (ServiceProvider services = Open(files.Path, clock, retain: false))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            Check((await store.EnqueueChain([Request("first"), Request("second")], "chain")).SequenceEqual(ids), "Chain dedupe lost.");
            JobLease first = (await store.Claim("node", TimeSpan.FromMinutes(1)))!;
            Check(first.Job.Id == ids[0], "Waiting/version filter lost.");
            await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero);
            Check((await store.Claim("node", TimeSpan.FromMinutes(1)))!.Job.Id == ids[1], "Chain successor not released.");
            await store.Cancel(version);
            await store.Maintain(100);
            Check(await store.Get(version) is null, "Immediate retention ignored.");
            clock.Advance(TimeSpan.FromSeconds(10));
            await store.Maintain(100);
            Check((await store.Search("cron")).TotalCount == 1 && await store.GetRecurringCount() == 2, "Schedules failed after reopening.");
        }
        await using (ServiceProvider services = Open(files.Path, clock, retain: false))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            Check(await store.EnqueueForCurrentInstance(Request("version"), "v1", "node") == version, "Version marker did not survive cleanup and reopen.");
            Check((await store.GetHistory()).Sum(p => p.Cancelled) == 1, "Retained history lost on reopen.");
        }
    }

    [Test]
    public async Task OnlyOneStoreCanOwnAFileAndConcurrentClaimsAreExclusive()
    {
        using var files = new Files();
        await using ServiceProvider first = Open(files.Path);
        await using ServiceProvider second = Open(files.Path);
        var store = first.GetRequiredService<FilesystemJobStore>();
        await store.Enqueue(Request());
        try { await second.GetRequiredService<IJobStore>().List(); throw new InvalidOperationException("Second owner opened the database."); }
        catch (IOException) { }
        JobLease?[] claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.Claim($"node-{i}", TimeSpan.FromMinutes(1))));
        Check(claims.Count(c => c is not null) == 1, "Concurrent claims duplicated work.");
    }

    [Test]
    public async Task PartialFileWritePreservesCommittedStateAndAllowsRetry()
    {
        using var files = new Files();
        await using ServiceProvider services = Open(files.Path);
        IFileUtil proxy = DispatchProxy.Create<IFileUtil, FailingFiles>();
        var failure = (FailingFiles)proxy;
        failure.Inner = services.GetRequiredService<IFileUtil>();
        await using var store = new FilesystemJobStore(new FlywheelFilesystemOptions { FilePath = files.Path }, proxy,
            services.GetRequiredService<IMemoryStreamUtil>(), services.GetRequiredService<ILogger<FilesystemJobStore>>());
        string id = await store.Enqueue(Request("committed"));
        string original = await File.ReadAllTextAsync(files.Path);
        failure.PartialWrite = true;
        try { await store.Enqueue(Request("uncommitted")); throw new InvalidOperationException("Failed write reported success."); }
        catch (IOException) { }
        Check(await File.ReadAllTextAsync(files.Path) == original && (await store.List()).Single().Id == id,
            "Failed atomic replacement published partial state.");
        failure.PartialWrite = false;
        await store.Enqueue(Request("retried"));
        Check((await store.List()).Count == 2, "Store could not retry a rejected commit.");
    }

    [Test]
    public async Task CorruptDatabaseIsRejectedWithoutOverwrite()
    {
        using var files = new Files();
        Directory.CreateDirectory(Path.GetDirectoryName(files.Path)!);
        await using ServiceProvider services = Open(files.Path);
        await services.GetRequiredService<IFileUtil>().Write(files.Path, "not valid json");
        bool failed = false;
        try { await services.GetRequiredService<IJobStore>().Enqueue(Request()); }
        catch (Exception) { failed = true; }
        Check(failed && await services.GetRequiredService<IFileUtil>().Read(files.Path) == "not valid json", "Corrupt database was silently replaced.");
    }

    [Test]
    public async Task ExpiredPersistedLeaseIsRecoveredAndOldOwnerIsFenced()
    {
        using var files = new Files();
        var clock = new Clock();
        JobLease lease;
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            await store.Enqueue(Request() with { Policy = new JobPolicy { MaxAttempts = 2 } });
            lease = (await store.Claim("old", TimeSpan.FromSeconds(1)))!;
        }
        clock.Advance(TimeSpan.FromSeconds(2));
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            Check(!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Expired persisted owner completed.");
            await store.Maintain(100);
            clock.Advance(TimeSpan.FromSeconds(5));
            JobLease? recovered = await store.Claim("new", TimeSpan.FromMinutes(1));
            Check(recovered is not null && recovered.Version > lease.Version && recovered.Job.Attempt == 2, "Persisted lease recovery failed.");
        }
    }

    [Test]
    public async Task NotificationsFollowPersistenceAndInvalidInputDoesNotPoisonStore()
    {
        using var files = new Files();
        await using ServiceProvider services = Open(files.Path);
        var store = services.GetRequiredService<FilesystemJobStore>();
        Check(ReferenceEquals(store, services.GetRequiredService<IJobStore>()), "DI registration split state.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<JobChange> changes = store.Watch(deadline.Token).GetAsyncEnumerator();
        Check(await changes.MoveNextAsync(), "Initial resync missing.");
        try { await store.Enqueue(Request() with { Payload = "invalid" }); throw new InvalidOperationException("Invalid JSON accepted."); }
        catch (System.Text.Json.JsonException) { }
        string id = await store.Enqueue(Request());
        Check(await changes.MoveNextAsync() && (await File.ReadAllTextAsync(files.Path)).Contains(id), "Notification preceded persisted state.");
    }
    private static FileSystemLibrarianDatabase OpenLibrarian(string path, ServiceProvider services) =>
        new(path, services.GetRequiredService<IFileUtil>(), services.GetRequiredService<IMemoryStreamUtil>(),
            services.GetRequiredService<ILogger<FilesystemJobStore>>());

    [Test]
    public async Task IndividualRecordsAreReadableThroughLibrarianAfterReopening()
    {
        using var files = new Files();
        string first, second;
        await using (ServiceProvider services = Open(files.Path))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            first = await store.Enqueue(Request("original", "case"));
            second = await store.Enqueue(Request("second", "CASE"));
            Check(first != second, "Case-sensitive Flywheel keys collided.");
        }
        await using ServiceProvider dependencies = Open(files.Path);
        await using (FileSystemLibrarianDatabase database = OpenLibrarian(files.Path, dependencies))
        {
            var jobs = await database.GetContainer("flywheel.jobs");
            var items = await jobs.GetLibrarianItems();
            Check(items.Count == 2 && items.Any(i => i.Value.Contains(first)) && items.Any(i => i.Value.Contains(second)),
                "Jobs were not stored as individual Librarian documents.");
            var format = await database.GetContainer("flywheel.control");
            Check(await format.GetItem("format") == "4" && await format.GetItem("state") is null, "Wrong storage format.");
        }
        Check((await dependencies.GetRequiredService<FilesystemJobStore>().Get(first))!.Name == "original", "Reopen lost a document.");
        Check(!File.Exists(files.Path + ".working"), "Old working-database engine is still in use.");
    }

    [Test]
    public async Task AbandonedWorkingAndCommitFilesNeverReplaceCommittedDatabase()
    {
        using var files = new Files();
        string id;
        await using (ServiceProvider services = Open(files.Path))
            id = await services.GetRequiredService<FilesystemJobStore>().Enqueue(Request("original"));
        string original = await File.ReadAllTextAsync(files.Path);
        await File.WriteAllTextAsync(files.Path + ".working", "partial write");
        await File.WriteAllTextAsync(files.Path + ".commit", original.Replace("original", "uncommitted"));
        await using ServiceProvider reopened = Open(files.Path);
        var store = reopened.GetRequiredService<FilesystemJobStore>();
        Check((await store.Get(id))!.Name == "original", "Abandoned files replaced committed state.");
        await store.Enqueue(Request("new"));
        Check((await store.List()).Count == 2 && (await store.Get(id))!.Name == "original", "Reopening failed to recover from abandoned files.");
    }

    [Test]
    public async Task LibrarianSaveFailureIsPropagatedBeforeCommit()
    {
        using var files = new Files();
        await using ServiceProvider services = Open(files.Path);
        IFileUtil proxy = DispatchProxy.Create<IFileUtil, FailingFiles>();
        var failure = (FailingFiles)proxy;
        failure.Inner = services.GetRequiredService<IFileUtil>();
        await using (var store = new FilesystemJobStore(new FlywheelFilesystemOptions { FilePath = files.Path }, proxy,
            services.GetRequiredService<IMemoryStreamUtil>(), services.GetRequiredService<ILogger<FilesystemJobStore>>()))
        {
            await store.Enqueue(Request("committed"));
            string original = await File.ReadAllTextAsync(files.Path);
            failure.FailWrites = true;
            bool rejected = false;
            try { await store.Enqueue(Request("discarded")); }
            catch (IOException) { rejected = true; }
            Check(rejected && failure.RejectedWrites > 0, "Librarian save failure was reported as success.");
            Check(await File.ReadAllTextAsync(files.Path) == original, "Failed Librarian save changed committed data.");
            Check((await store.List()).Single().Name == "committed", "Failed commit exposed staged documents.");
        }
        Check((await services.GetRequiredService<FilesystemJobStore>().List()).Single().Name == "committed", "Failed save escaped into reopened state.");
    }

    [Test]
    public async Task MidMutationFailureDiscardsStagedDocuments()
    {
        using var files = new Files();
        var clock = new Clock();
        string id;
        await using (ServiceProvider initial = Open(files.Path, clock))
            id = await initial.GetRequiredService<FilesystemJobStore>().Enqueue(Request("committed"));
        string original = await File.ReadAllTextAsync(files.Path);
        await using (ServiceProvider dependencies = Open(files.Path, clock))
        await using (FileSystemLibrarianDatabase database = OpenLibrarian(files.Path, dependencies))
        {
            var history = await database.GetContainer("flywheel.history");
            await history.UpdateItemStrict((await history.GetAllIds()).Single(), "not valid json");
            await database.Save();
        }
        string corrupted = await File.ReadAllTextAsync(files.Path);
        await using (ServiceProvider failed = Open(files.Path, clock))
        {
            bool rejected = false;
            try { await failed.GetRequiredService<FilesystemJobStore>().Enqueue(Request("discarded", "retry")); }
            catch (System.Text.Json.JsonException) { rejected = true; }
            Check(rejected && await File.ReadAllTextAsync(files.Path) == corrupted, "Partial mutation changed committed data.");
        }
        await File.WriteAllTextAsync(files.Path, original);
        await using ServiceProvider reopened = Open(files.Path, clock);
        var store = reopened.GetRequiredService<FilesystemJobStore>();
        Check((await store.List()).Single().Id == id, "Failed mutation escaped after reopen.");
        string retry = await store.Enqueue(Request("retried", "retry"));
        Check((await store.Get(retry))!.Name == "retried", "Rollback retained a dedupe marker.");
    }

    [Test]
    public async Task ReadsDoNotRewriteCommittedDatabase()
    {
        using var files = new Files();
        await using ServiceProvider services = Open(files.Path);
        var store = services.GetRequiredService<FilesystemJobStore>();
        string id = await store.Enqueue(Request());
        File.SetLastWriteTimeUtc(files.Path, DateTime.UtcNow.AddDays(-1));
        DateTime modified = File.GetLastWriteTimeUtc(files.Path);
        string original = await File.ReadAllTextAsync(files.Path);
        Check((await store.Get(id))!.Id == id && (await store.List()).Count == 1, "Read failed.");
        Check(File.GetLastWriteTimeUtc(files.Path) == modified && await File.ReadAllTextAsync(files.Path) == original, "Read rewrote the committed database.");
    }
    [Test]
    public async Task SearchPaginationPreservesOrderingAndTotalCount()
    {
        using var files = new Files();
        var clock = new Clock();
        await using ServiceProvider services = Open(files.Path, clock);
        var store = services.GetRequiredService<FilesystemJobStore>();
        var ids = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            ids.Add(await store.Enqueue(Request("match")));
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var page = await store.Search("match", 2, 3);
        Check(page.TotalCount == 8 && page.Items.Select(j => j.Id).SequenceEqual(ids.AsEnumerable().Reverse().Skip(2).Take(3)), "Search count or page ordering changed.");
        Check((await store.List(2, 3)).Select(j => j.Id).SequenceEqual(ids.AsEnumerable().Reverse().Skip(2).Take(3)), "Pagination order changed.");
    }
    [Test]
    public async Task LibrarianLoadFailureCannotOverwriteExistingItems()
    {
        using var files = new Files();
        await using ServiceProvider services = Open(files.Path);
        IFileUtil proxy = DispatchProxy.Create<IFileUtil, FailingFiles>();
        var failure = (FailingFiles)proxy;
        failure.Inner = services.GetRequiredService<IFileUtil>();
        await using var store = new FilesystemJobStore(new FlywheelFilesystemOptions { FilePath = files.Path }, proxy,
            services.GetRequiredService<IMemoryStreamUtil>(), services.GetRequiredService<ILogger<FilesystemJobStore>>());
        await store.Enqueue(Request("committed"));
        string original = await File.ReadAllTextAsync(files.Path);
        failure.FailReads = true;
        bool rejected = false;
        try { await store.Enqueue(Request("discarded")); }
        catch (IOException) { rejected = true; }
        Check(rejected && await File.ReadAllTextAsync(files.Path) == original, "Librarian load failure lost existing items.");
        failure.FailReads = false;
        Check((await store.List()).Single().Name == "committed", "Load failure exposed an empty container.");
    }

}
