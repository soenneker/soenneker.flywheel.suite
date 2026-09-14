# Redis storage

`Soenneker.Flywheel.Redis` uses `Soenneker.Librarian.Redis`, backed by `Soenneker.Redis.Client`. Documents are stored and queried directly in Redis. Flywheel holds only an operation's staged writes and query results; there is no local database mirror or periodic save.

```csharp
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Redis;

builder.Services.AddFlywheel().AddRedis(options =>
{
    options.ConnectionString = "localhost:6379";
    options.Namespace = "my-app";
    options.KeyPrefix = "flywheel"; // Default top-level Redis prefix; configurable.
    options.Database = 0;
    options.HistoryRetention = TimeSpan.FromDays(30);
    options.RetainCompletedJobs = true;
});
```

Workers sharing jobs must use the same database, namespace, storage version, and job registrations. Redis persistence and `maxmemory-policy noeviction` are required when losing jobs is unacceptable. Librarian uses native hashes, sets, sorted sets, sorting, and conditional transactions. No Lua, `EVAL`, or script permission is required. The provider reads Redis server time from the primary owning its namespace slot.

Redis Cluster deployments require Redis 8 or later for Librarian's sorting with external key patterns.

## JSON storage contracts

Workers sharing data must also use the same `KeyPrefix`. Redis keys appear as `flywheel:{my-app}:containers:flywheel.jobs:document:ID`, with the readable `options.Namespace` inside braces, readable container names, and readable index paths. Set `options.KeyPrefix` to change the top-level folder. Document IDs remain Flywheel's key hashes. Reserved characters in key segments are escaped. This layout does not migrate or read the previous hex-encoded keys.

Each record is a Librarian document in a `flywheel.*` container, serialized through `Soenneker.Utils.Json`. Envelopes contain a typed key and value; job state and priority retain numeric JSON values. Hashed IDs preserve case-sensitive Flywheel keys. Namespace-wide Redis hash tags let a single transaction update multiple containers in Redis Cluster. Consequently, a namespace resides on one Cluster slot.

Conditional batches commit jobs, lease metadata, rate usage, chains, history, and notifications together. Per-table revisions protect reads and query selection from concurrent mutations, including phantom inserts. Rejected attempts are retried with bounded jitter. Caller cancellation or a lost connection after dispatch can leave the outcome uncertain; reconcile stored state before retrying non-idempotent submissions. Use idempotency keys where appropriate. Only Librarian may manage these keys; Redis command execution errors do not provide transaction rollback.

## Dispatch index

The `flywheel.dispatch` container contains compact candidate documents without job payloads. Librarian's due-time index selects eligible candidates in Redis; Flywheel compares their priority and version restrictions and fetches the selected job. The `flywheel.running` container supports method concurrency checks without downloading job payloads. Both are updated atomically with jobs. Missing or inconsistent dispatch metadata in a validated snapshot raises an error.

Job list pagination uses an indexed composite timestamp/ID key. State, owner, name, and time-range queries use Librarian indexes. Free-text substring matching and aggregate shaping remain application-side over the selected documents. Dispatch still examines due candidates to preserve global priority and version matching, so its cost grows with eligible backlog size. Previous benchmark results describe the old storage engine and must be rerun.

## Notifications and retention

A notification document is written in the same transaction as its changes. `Watch` polls that document every 100 milliseconds, works across instances, and emits `Resync` on initial subscription or a detected revision gap. It is an invalidation feed, not a durable event stream; consumers refresh authoritative state after `Resync`. Redis Pub/Sub permission is no longer needed. Unchanged-capacity heartbeats and live samples do not publish dashboard invalidations.

The hosted recorder samples active work every second and sleeps while idle. Empty seconds can be reconstructed only while the lifecycle revision still matches the recorded empty state. Expired samples are excluded from reads and pruned by sampling or maintenance. Retention is implemented through Librarian document deletion rather than Redis key TTLs: stopped workers do not perform cleanup. Completed-job retention defaults to one day; disabling it removes terminal documents on the next maintenance pass. Version markers survive job cleanup.

## Upgrading existing data

Flywheel uses Librarian document format 4.
