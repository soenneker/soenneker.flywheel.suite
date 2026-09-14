# Filesystem storage

`Soenneker.Flywheel.Filesystem` uses `Soenneker.Librarian.FileSystem` for individual document storage, indexes, and atomic commits. Registration remains the same:

```csharp
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Filesystem;

builder.Services.AddFlywheel().AddFilesystem(options =>
{
    options.FilePath = Path.Combine(AppContext.BaseDirectory, "data", "flywheel.librarian.json");
    options.HistoryRetention = TimeSpan.FromDays(1);
    options.RetainCompletedJobs = true;
});
```

Choose one provider per runtime. The default database file is `flywheel.json` in the application directory.

## Documents and transactions

Jobs, dispatch candidates, running leases, schedules, policies, rate usage, version markers, logs, history, samples, and other records have separate `flywheel.*` containers. Records contain a typed key and value. Hashed document IDs preserve Flywheel's case-sensitive keys over Librarian's case-insensitive IDs. Control documents hold format, concurrency revisions, and the latest committed notification batch.

Flywheel stages only the writes for an operation. Librarian atomically persists the full database file before publishing those writes in memory. A failed write leaves committed state intact and can be retried after the failure is resolved. Reads of an initialized database do not rewrite it. Cancellation before commit discards the attempt; once persistence commits, publication completes. An uncertain I/O outcome must be reconciled against stored state before retrying a non-idempotent operation.

Librarian retains loaded containers and indexes in memory. Batches clone changed containers and rebuild their explicit indexes, and persistence rewrites the complete JSON database. This provider suits local applications and modest workloads; it is not a streaming disk engine.

## Ownership and recovery

One store exclusively owns the path through an open `.lock` file. Other stores or processes cannot open the same path while it is owned. Stop workers before editing or backing up the database; shared network filesystems and aliases to the same file are unsupported. The lock file remains on disk, but the open handle controls ownership. Asynchronously dispose the service provider to release it.

Jobs keep their fencing versions and lease tokens across restarts. Normal maintenance recovers expired leases and removes expired records. Version-submission markers survive completed-job cleanup. `RetainCompletedJobs = false` removes terminal documents on the next maintenance pass. Logs remain bounded to 1,000 entries per job.

Atomic replacement and file flushing depend on the filesystem and OS for power-loss durability. No separate database server is required.
