using Soenneker.Utils.MemoryStream;
using Microsoft.Extensions.Logging.Abstractions;
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
using System.Threading;

namespace Soenneker.Flywheel.Filesystem.Tests;

public sealed class FilesystemJobStoreTests
{
    private static readonly IFileUtil _fileUtil = new Soenneker.Utils.File.FileUtil(NullLogger<Soenneker.Utils.File.FileUtil>.Instance, new MemoryStreamUtil());

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
    public async ValueTask KeyedDatabaseKeepsOwnershipUntilProviderDisposal(CancellationToken cancellationToken)
    {
        using var files = new Files();
        await using (ServiceProvider services = Open(files.Path))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            await store.Enqueue(Request(), cancellationToken: cancellationToken);
            var database = services.GetRequiredKeyedService<FileSystemLibrarianDatabase>(FilesystemRegistration.LibrarianServiceKey);
            Check(services.GetService<FileSystemLibrarianDatabase>() is null, "Flywheel database was registered without a key.");
            Check((await (await database.GetContainer("flywheel.jobs", cancellationToken: cancellationToken)).GetLibrarianItems(cancellationToken: cancellationToken)).Count == 1,
                "The store did not use the keyed database.");
            await store.DisposeAsync();
            await database.GetContainer("still-alive", cancellationToken: cancellationToken);
            bool locked = false;
            try { using var competing = new FileStream(files.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { locked = true; }
            Check(locked, "The store released ownership while the DI-owned database was still alive.");
        }
        using var released = new FileStream(files.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Test]
    public async ValueTask ReleaseIsolationUsesSeparateFilesAndOwnershipLocks(CancellationToken cancellationToken)
    {
        using var files = new Files();
        ServiceProvider Release(string version)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddFlywheel(o => { o.IsolateApplicationVersion = true; o.ApplicationVersion = version; })
                .AddFilesystem(o => o.FilePath = files.Path);
            return services.BuildServiceProvider();
        }
        string id;
        await using (var first = Release("v1"))
        await using (var second = Release("v2"))
        await using (var legacy = Open(files.Path))
        {
            var newStore = second.GetRequiredService<FilesystemJobStore>();
            id = await newStore.Enqueue(Request(key: "same"), cancellationToken: cancellationToken);
            var oldStore = first.GetRequiredService<FilesystemJobStore>();
            Check(await oldStore.Claim("old", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is null, "Old release claimed new work.");
            Check(await oldStore.Enqueue(Request(key: "same"), cancellationToken: cancellationToken) != id, "Cross-release deduplication collided.");
            Check(await legacy.GetRequiredService<FilesystemJobStore>().Get(id, cancellationToken: cancellationToken) is null, "Legacy file exposed isolated work.");
        }
        await using var reopened = Release("v2");
        Check((await reopened.GetRequiredService<FilesystemJobStore>().Claim("new", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!.Job.Id == id,
            "Release path was not stable across restarts.");
    }

    [Test]
    public async ValueTask ServerWorkerHistorySurvivesReopening(CancellationToken cancellationToken)
    {
        using var files = new Files();
        var clock = new Clock();
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            await store.Enqueue(Request(), cancellationToken: cancellationToken);
            await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
            await store.Heartbeat("node", 4, TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var server = await services.GetRequiredService<FilesystemJobStore>().GetServer("node", cancellationToken: cancellationToken);
            Check(server is not null && server.WorkerHistory.Single().BusyWorkers == 1,
                "Stored worker history must survive reopening without a dashboard session.");
        }
    }
    [Test]
    public async ValueTask JobsLeasesLogsProgressAndLimitsSurviveReopening(CancellationToken cancellationToken)
    {
        using var files = new Files();
        var clock = new Clock();
        JobLease lease;
        string id;
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            await store.ConfigureMethod("job", new MethodPolicy { MaxConcurrency = 1, RateLimit = 1, RateWindow = TimeSpan.FromMinutes(1) }, cancellationToken: cancellationToken);
            id = await store.Enqueue(Request(key: "key"), cancellationToken: cancellationToken);
            lease = (await store.Claim("node", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken))!;
            await store.SetProgress(lease, 25, "progress", cancellationToken: cancellationToken);
            await store.AppendLogs(lease, [new JobLogMessage("Info", "test", "persist me")], cancellationToken: cancellationToken);
            await store.Heartbeat("node", 2, TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
            Check(await services.GetRequiredService<IFileUtil>().Exists(files.Path, cancellationToken: cancellationToken) && (await _fileUtil.Read(files.Path, cancellationToken: cancellationToken)).Contains(id), "Enqueue was not persisted before returning.");
        }
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            JobRecord job = (await store.Get(id, cancellationToken: cancellationToken))!;
            Check(job.State == JobState.Running && job.Progress == 25 && job.Token == lease.Token, "Lease or progress did not round trip.");
            Check((await store.GetLogs(id, cancellationToken: cancellationToken)).Single().Message == "persist me", "Log did not round trip.");
            Check(await store.GetTotalWorkerCount(cancellationToken: cancellationToken) == 2, "Heartbeat did not round trip.");
            Check(await store.Enqueue(Request(key: "key"), cancellationToken: cancellationToken) == id, "Dedupe lost across reopen.");
            await store.Enqueue(Request(), cancellationToken: cancellationToken);
            Check(await store.Claim("node2", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is null, "Concurrency policy lost across reopen.");
            Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Persisted lease could not finish.");
            Check(await store.Claim("node2", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken) is null, "Rate usage lost across reopen.");
            Check((await store.GetSearchHistory(null, null, null, cancellationToken: cancellationToken)).Sum(p => p.Succeeded) == 1, "Search history lost.");
        }
    }

    [Test]
    public async ValueTask ChainsSchedulesAndVersionMarkersSurviveReopening(CancellationToken cancellationToken)
    {
        using var files = new Files();
        var clock = new Clock();
        IReadOnlyList<string> ids;
        string version;
        await using (ServiceProvider services = Open(files.Path, clock, retain: false))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            ids = await store.EnqueueChain([Request("first"), Request("second")], "chain", cancellationToken: cancellationToken);
            version = await store.EnqueueForCurrentInstance(Request("version"), "v1", "node", cancellationToken: cancellationToken);
            await store.AddRecurring("interval", Request("recurring"), TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
            await store.AddCron("cron", Request("cron"), "*/10 * * * * *", includeSeconds: true, cancellationToken: cancellationToken);
        }
        await using (ServiceProvider services = Open(files.Path, clock, retain: false))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            Check((await store.EnqueueChain([Request("first"), Request("second")], "chain", cancellationToken: cancellationToken)).SequenceEqual(ids), "Chain dedupe lost.");
            JobLease first = (await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!;
            Check(first.Job.Id == ids[0], "Waiting/version filter lost.");
            await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken);
            Check((await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken))!.Job.Id == ids[1], "Chain successor not released.");
            await store.Cancel(version, cancellationToken: cancellationToken);
            await store.Maintain(100, cancellationToken: cancellationToken);
            Check(await store.Get(version, cancellationToken: cancellationToken) is null, "Immediate retention ignored.");
            clock.Advance(TimeSpan.FromSeconds(10));
            await store.Maintain(100, cancellationToken: cancellationToken);
            Check((await store.Search("cron", cancellationToken: cancellationToken)).TotalCount == 1 && await store.GetRecurringCount(cancellationToken: cancellationToken) == 2, "Schedules failed after reopening.");
        }
        await using (ServiceProvider services = Open(files.Path, clock, retain: false))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            Check(await store.EnqueueForCurrentInstance(Request("version"), "v1", "node", cancellationToken: cancellationToken) == version, "Version marker did not survive cleanup and reopen.");
            Check((await store.GetHistory(cancellationToken: cancellationToken)).Sum(p => p.Cancelled) == 1, "Retained history lost on reopen.");
        }
    }

    [Test]
    public async ValueTask OnlyOneStoreCanOwnAFileAndConcurrentClaimsAreExclusive(CancellationToken cancellationToken)
    {
        using var files = new Files();
        await using ServiceProvider first = Open(files.Path);
        await using ServiceProvider second = Open(files.Path);
        var store = first.GetRequiredService<FilesystemJobStore>();
        await store.Enqueue(Request(), cancellationToken: cancellationToken);
        try { await second.GetRequiredService<IJobStore>().List(cancellationToken: cancellationToken); throw new InvalidOperationException("Second owner opened the database."); }
        catch (IOException) { }
        JobLease?[] claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.Claim($"node-{i}", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken)));
        Check(claims.Count(c => c is not null) == 1, "Concurrent claims duplicated work.");
    }

    [Test]
    public async ValueTask PartialFileWritePreservesCommittedStateAndAllowsRetry(CancellationToken cancellationToken)
    {
        using var files = new Files();
        await using ServiceProvider services = Open(files.Path);
        IFileUtil proxy = DispatchProxy.Create<IFileUtil, FailingFiles>();
        var failure = (FailingFiles)proxy;
        failure.Inner = services.GetRequiredService<IFileUtil>();
        await using var store = new FilesystemJobStore(new FlywheelFilesystemOptions { FilePath = files.Path }, proxy,
            services.GetRequiredService<IMemoryStreamUtil>(), services.GetRequiredService<ILogger<FilesystemJobStore>>());
        string id = await store.Enqueue(Request("committed"), cancellationToken: cancellationToken);
        string original = await _fileUtil.Read(files.Path, cancellationToken: cancellationToken);
        failure.PartialWrite = true;
        try { await store.Enqueue(Request("uncommitted"), cancellationToken: cancellationToken); throw new InvalidOperationException("Failed write reported success."); }
        catch (IOException) { }
        Check(await _fileUtil.Read(files.Path, cancellationToken: cancellationToken) == original && (await store.List(cancellationToken: cancellationToken)).Single().Id == id,
            "Failed atomic replacement published partial state.");
        failure.PartialWrite = false;
        await store.Enqueue(Request("retried"), cancellationToken: cancellationToken);
        Check((await store.List(cancellationToken: cancellationToken)).Count == 2, "Store could not retry a rejected commit.");
    }

    [Test]
    public async ValueTask CorruptDatabaseIsRejectedWithoutOverwrite(CancellationToken cancellationToken)
    {
        using var files = new Files();
        Directory.CreateDirectory(Path.GetDirectoryName(files.Path)!);
        await using ServiceProvider services = Open(files.Path);
        await services.GetRequiredService<IFileUtil>().Write(files.Path, "not valid json", cancellationToken: cancellationToken);
        bool failed = false;
        try { await services.GetRequiredService<IJobStore>().Enqueue(Request(), cancellationToken: cancellationToken); }
        catch (Exception) { failed = true; }
        Check(failed && await services.GetRequiredService<IFileUtil>().Read(files.Path, cancellationToken: cancellationToken) == "not valid json", "Corrupt database was silently replaced.");
    }

    [Test]
    public async ValueTask ExpiredPersistedLeaseIsRecoveredAndOldOwnerIsFenced(CancellationToken cancellationToken)
    {
        using var files = new Files();
        var clock = new Clock();
        JobLease lease;
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            await store.Enqueue(Request() with { Policy = new JobPolicy { MaxAttempts = 2 } }, cancellationToken: cancellationToken);
            lease = (await store.Claim("old", TimeSpan.FromSeconds(1), cancellationToken: cancellationToken))!;
        }
        clock.Advance(TimeSpan.FromSeconds(2));
        await using (ServiceProvider services = Open(files.Path, clock))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            Check(!await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Expired persisted owner completed.");
            await store.Maintain(100, cancellationToken: cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(5));
            JobLease? recovered = await store.Claim("new", TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
            Check(recovered is not null && recovered.Version > lease.Version && recovered.Job.Attempt == 2, "Persisted lease recovery failed.");
        }
    }

    [Test]
    public async ValueTask NotificationsFollowPersistenceAndInvalidInputDoesNotPoisonStore(CancellationToken cancellationToken)
    {
        using var files = new Files();
        await using ServiceProvider services = Open(files.Path);
        var store = services.GetRequiredService<FilesystemJobStore>();
        Check(ReferenceEquals(store, services.GetRequiredService<IJobStore>()), "DI registration split state.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<JobChange> changes = store.Watch(deadline.Token).GetAsyncEnumerator(cancellationToken: cancellationToken);
        Check(await changes.MoveNextAsync(), "Initial resync missing.");
        try { await store.Enqueue(Request() with { Payload = "invalid" }, cancellationToken: cancellationToken); throw new InvalidOperationException("Invalid JSON accepted."); }
        catch (System.Text.Json.JsonException) { }
        string id = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        Check(await changes.MoveNextAsync() && (await _fileUtil.Read(files.Path, cancellationToken: cancellationToken)).Contains(id), "Notification preceded persisted state.");
    }
    private static FileSystemLibrarianDatabase OpenLibrarian(string path, ServiceProvider services) =>
        new(path, services.GetRequiredService<IFileUtil>(), services.GetRequiredService<IMemoryStreamUtil>(),
            services.GetRequiredService<ILogger<FilesystemJobStore>>());

    [Test]
    public async ValueTask IndividualRecordsAreReadableThroughLibrarianAfterReopening(CancellationToken cancellationToken)
    {
        using var files = new Files();
        string first, second;
        await using (ServiceProvider services = Open(files.Path))
        {
            var store = services.GetRequiredService<FilesystemJobStore>();
            first = await store.Enqueue(Request("original", "case"), cancellationToken: cancellationToken);
            second = await store.Enqueue(Request("second", "CASE"), cancellationToken: cancellationToken);
            Check(first != second, "Case-sensitive Flywheel keys collided.");
        }
        await using ServiceProvider dependencies = Open(files.Path);
        await using (FileSystemLibrarianDatabase database = OpenLibrarian(files.Path, dependencies))
        {
            var jobs = await database.GetContainer("flywheel.jobs", cancellationToken: cancellationToken);
            var items = await jobs.GetLibrarianItems(cancellationToken: cancellationToken);
            Check(items.Count == 2 && items.Any(i => i.Value.Contains(first)) && items.Any(i => i.Value.Contains(second)),
                "Jobs were not stored as individual Librarian documents.");
            var format = await database.GetContainer("flywheel.control", cancellationToken: cancellationToken);
            Check(await format.GetItem("format", cancellationToken: cancellationToken) == "4" && await format.GetItem("state", cancellationToken: cancellationToken) is null, "Wrong storage format.");
        }
        Check((await dependencies.GetRequiredService<FilesystemJobStore>().Get(first, cancellationToken: cancellationToken))!.Name == "original", "Reopen lost a document.");
        Check(!(await _fileUtil.Exists(files.Path + ".working", cancellationToken: cancellationToken)), "Old working-database engine is still in use.");
    }

    [Test]
    public async ValueTask AbandonedWorkingAndCommitFilesNeverReplaceCommittedDatabase(CancellationToken cancellationToken)
    {
        using var files = new Files();
        string id;
        await using (ServiceProvider services = Open(files.Path))
            id = await services.GetRequiredService<FilesystemJobStore>().Enqueue(Request("original"), cancellationToken: cancellationToken);
        string original = await _fileUtil.Read(files.Path, cancellationToken: cancellationToken);
        await _fileUtil.Write(files.Path + ".working", "partial write", cancellationToken: cancellationToken);
        await _fileUtil.Write(files.Path + ".commit", original.Replace("original", "uncommitted"), cancellationToken: cancellationToken);
        await using ServiceProvider reopened = Open(files.Path);
        var store = reopened.GetRequiredService<FilesystemJobStore>();
        Check((await store.Get(id, cancellationToken: cancellationToken))!.Name == "original", "Abandoned files replaced committed state.");
        await store.Enqueue(Request("new"), cancellationToken: cancellationToken);
        Check((await store.List(cancellationToken: cancellationToken)).Count == 2 && (await store.Get(id, cancellationToken: cancellationToken))!.Name == "original", "Reopening failed to recover from abandoned files.");
    }

    [Test]
    public async ValueTask LibrarianSaveFailureIsPropagatedBeforeCommit(CancellationToken cancellationToken)
    {
        using var files = new Files();
        await using ServiceProvider services = Open(files.Path);
        IFileUtil proxy = DispatchProxy.Create<IFileUtil, FailingFiles>();
        var failure = (FailingFiles)proxy;
        failure.Inner = services.GetRequiredService<IFileUtil>();
        await using (var store = new FilesystemJobStore(new FlywheelFilesystemOptions { FilePath = files.Path }, proxy,
            services.GetRequiredService<IMemoryStreamUtil>(), services.GetRequiredService<ILogger<FilesystemJobStore>>()))
        {
            await store.Enqueue(Request("committed"), cancellationToken: cancellationToken);
            string original = await _fileUtil.Read(files.Path, cancellationToken: cancellationToken);
            failure.FailWrites = true;
            bool rejected = false;
            try { await store.Enqueue(Request("discarded"), cancellationToken: cancellationToken); }
            catch (IOException) { rejected = true; }
            Check(rejected && failure.RejectedWrites > 0, "Librarian save failure was reported as success.");
            Check(await _fileUtil.Read(files.Path, cancellationToken: cancellationToken) == original, "Failed Librarian save changed committed data.");
            Check((await store.List(cancellationToken: cancellationToken)).Single().Name == "committed", "Failed commit exposed staged documents.");
        }
        Check((await services.GetRequiredService<FilesystemJobStore>().List(cancellationToken: cancellationToken)).Single().Name == "committed", "Failed save escaped into reopened state.");
    }

    [Test]
    public async ValueTask MidMutationFailureDiscardsStagedDocuments(CancellationToken cancellationToken)
    {
        using var files = new Files();
        var clock = new Clock();
        string id;
        await using (ServiceProvider initial = Open(files.Path, clock))
            id = await initial.GetRequiredService<FilesystemJobStore>().Enqueue(Request("committed"), cancellationToken: cancellationToken);
        string original = await _fileUtil.Read(files.Path, cancellationToken: cancellationToken);
        await using (ServiceProvider dependencies = Open(files.Path, clock))
        await using (FileSystemLibrarianDatabase database = OpenLibrarian(files.Path, dependencies))
        {
            var history = await database.GetContainer("flywheel.history", cancellationToken: cancellationToken);
            await history.UpdateItemStrict((await history.GetAllIds(cancellationToken: cancellationToken)).Single(), "not valid json", cancellationToken: cancellationToken);
            await database.Save(cancellationToken: cancellationToken);
        }
        string corrupted = await _fileUtil.Read(files.Path, cancellationToken: cancellationToken);
        await using (ServiceProvider failed = Open(files.Path, clock))
        {
            bool rejected = false;
            try { await failed.GetRequiredService<FilesystemJobStore>().Enqueue(Request("discarded", "retry"), cancellationToken: cancellationToken); }
            catch (System.Text.Json.JsonException) { rejected = true; }
            Check(rejected && await _fileUtil.Read(files.Path, cancellationToken: cancellationToken) == corrupted, "Partial mutation changed committed data.");
        }
        await _fileUtil.Write(files.Path, original, cancellationToken: cancellationToken);
        await using ServiceProvider reopened = Open(files.Path, clock);
        var store = reopened.GetRequiredService<FilesystemJobStore>();
        Check((await store.List(cancellationToken: cancellationToken)).Single().Id == id, "Failed mutation escaped after reopen.");
        string retry = await store.Enqueue(Request("retried", "retry"), cancellationToken: cancellationToken);
        Check((await store.Get(retry, cancellationToken: cancellationToken))!.Name == "retried", "Rollback retained a dedupe marker.");
    }

    [Test]
    public async ValueTask ReadsDoNotRewriteCommittedDatabase(CancellationToken cancellationToken)
    {
        using var files = new Files();
        await using ServiceProvider services = Open(files.Path);
        var store = services.GetRequiredService<FilesystemJobStore>();
        string id = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        await _fileUtil.SetLastWriteTimeUtc(files.Path, DateTime.UtcNow.AddDays(-1), cancellationToken: cancellationToken);
        DateTime modified = (await _fileUtil.GetLastModified(files.Path, cancellationToken: cancellationToken))!.Value.UtcDateTime;
        string original = await _fileUtil.Read(files.Path, cancellationToken: cancellationToken);
        Check((await store.Get(id, cancellationToken: cancellationToken))!.Id == id && (await store.List(cancellationToken: cancellationToken)).Count == 1, "Read failed.");
        Check((await _fileUtil.GetLastModified(files.Path, cancellationToken: cancellationToken))!.Value.UtcDateTime == modified && await _fileUtil.Read(files.Path, cancellationToken: cancellationToken) == original, "Read rewrote the committed database.");
    }
    [Test]
    public async ValueTask SearchPaginationPreservesOrderingAndTotalCount(CancellationToken cancellationToken)
    {
        using var files = new Files();
        var clock = new Clock();
        await using ServiceProvider services = Open(files.Path, clock);
        var store = services.GetRequiredService<FilesystemJobStore>();
        var ids = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            ids.Add(await store.Enqueue(Request("match"), cancellationToken: cancellationToken));
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var page = await store.Search("match", 2, 3, cancellationToken: cancellationToken);
        Check(page.TotalCount == 8 && page.Items.Select(j => j.Id).SequenceEqual(ids.AsEnumerable().Reverse().Skip(2).Take(3)), "Search count or page ordering changed.");
        Check((await store.List(2, 3, cancellationToken: cancellationToken)).Select(j => j.Id).SequenceEqual(ids.AsEnumerable().Reverse().Skip(2).Take(3)), "Pagination order changed.");
    }
    [Test]
    public async ValueTask LibrarianLoadFailureCannotOverwriteExistingItems(CancellationToken cancellationToken)
    {
        using var files = new Files();
        await using ServiceProvider services = Open(files.Path);
        IFileUtil proxy = DispatchProxy.Create<IFileUtil, FailingFiles>();
        var failure = (FailingFiles)proxy;
        failure.Inner = services.GetRequiredService<IFileUtil>();
        await using var store = new FilesystemJobStore(new FlywheelFilesystemOptions { FilePath = files.Path }, proxy,
            services.GetRequiredService<IMemoryStreamUtil>(), services.GetRequiredService<ILogger<FilesystemJobStore>>());
        await store.Enqueue(Request("committed"), cancellationToken: cancellationToken);
        string original = await _fileUtil.Read(files.Path, cancellationToken: cancellationToken);
        failure.FailReads = true;
        bool rejected = false;
        try { await store.Enqueue(Request("discarded"), cancellationToken: cancellationToken); }
        catch (IOException) { rejected = true; }
        Check(rejected && await _fileUtil.Read(files.Path, cancellationToken: cancellationToken) == original, "Librarian load failure lost existing items.");
        failure.FailReads = false;
        Check((await store.List(cancellationToken: cancellationToken)).Single().Name == "committed", "Load failure exposed an empty container.");
    }

}
