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

To use a multiplexer already managed by your application, pass a connection factory to `AddRedis`. For example, with a keyed singleton registered as `IConnectionMultiplexer`:

```csharp
builder.Services.AddFlywheel().AddRedis(
    options => options.Namespace = "my-app",
    connectionFactory: (services, ct) =>
        ValueTask.FromResult(services.GetRequiredKeyedService<IConnectionMultiplexer>("jobs")));
```

The factory takes precedence over `ConnectionString`. Return a shared connection and let its owner dispose it. Flywheel selects `options.Database` on that connection.

Workers sharing jobs must use the same database, namespace, storage version, and job registrations. Redis persistence and `maxmemory-policy noeviction` are required when losing jobs is unacceptable. Librarian uses hashes, sets, sorted sets, conditional transactions, and atomic Lua scripts. Redis ACLs must allow `EVAL` and `EVALSHA` as well as the commands executed by those scripts. The provider reads server time from the primary owning its namespace slot.

The local Librarian compatibility implementation removes `SORT` and external-key patterns. It supports standalone Garnet with `--lua --lua-transaction-mode`; both switches are required for correct coordination. Configure Garnet persistence, commit acknowledgment, and recovery separately. Cluster failover and production throughput require deployment-specific validation.

Until a Librarian package containing this implementation is published and referenced here, test it using the sibling Librarian checkout:

```powershell
$env:FLYWHEEL_TEST_REDIS = "localhost:16387"
dotnet test --project test/Soenneker.Flywheel.Redis.Tests -p:UseLocalLibrarianProjects=true -- --treenode-filter "/*/*/FlywheelRedisTests/*"
```

## JSON storage contracts

Workers sharing data must also use the same `KeyPrefix`. Redis keys appear as `flywheel:{my-app}:containers:flywheel.jobs:document:ID`, with the readable `options.Namespace` inside braces, readable container names, and readable index paths. Set `options.KeyPrefix` to change the top-level folder. Document IDs remain Flywheel's key hashes. Reserved characters in key segments are escaped. This layout does not migrate or read the previous hex-encoded keys.

Each record is a Librarian document in a `flywheel.*` container, serialized through `Soenneker.Utils.Json`. Envelopes contain a typed key and value; job state and priority retain numeric JSON values. Hashed IDs preserve case-sensitive Flywheel keys. Namespace-wide Redis hash tags let a single transaction update multiple containers in Redis Cluster. Consequently, a namespace resides on one Cluster slot.

Conditional batches commit jobs, lease metadata, rate usage, chains, history, and notifications together. Per-table revisions protect reads and query selection from concurrent mutations, including phantom inserts. Rejected attempts are retried with bounded jitter. Caller cancellation or a lost connection after dispatch can leave the outcome uncertain; reconcile stored state before retrying non-idempotent submissions. Use idempotency keys where appropriate. Only Librarian may manage these keys; Redis command execution errors do not provide transaction rollback.

## Dispatch index

The `flywheel.dispatch` container indexes compact candidates by queue rank, priority, due time and job ID. Claims probe the ordered head first and check worker eligibility, method limits and partition fairness before granting a lease. Sparse eligibility, partition fairness and older ordering records can require more work. Claims validate the selected job and fetch its payload. Running counts use `flywheel.running`.

Unfiltered dashboard pages use a running-first timestamp/ID index when every job has the ordering field. Filtered searches read pages of searchable fields and load full records for the requested results. Exact substring counts still require scanning candidates.

## Slow Redis and observability

`StorageOperationTimeout` includes admission and optimistic retries. A submitted atomic commit can outlive the caller's deadline. Lease renewal retries transient storage errors within the last confirmed lease, using a monotonic clock and reserving time to stop execution. An explicit loss of ownership cancels execution immediately. Each completion write checks the lease token and version.

Built-in workers send heartbeats independently of maintenance. Maintenance checks for due work, then commits recovery, scheduled occurrences and cleanup in bounded chunks. A later timeout preserves earlier commits. Normal operations, lease renewals, fenced completions, snapshot reads and heartbeats have separate admission queues. Each admits up to 1,024 active and waiting calls per store instance. Dashboard search admits four concurrent scans and queues up to 128 more within their original deadlines.

The `Soenneker.Flywheel.Storage` meter exposes gate wait, transaction duration, conflicts, timeouts, rejected admissions, lease headroom, heartbeat age, schedule lag and log flush failures. The sibling Redis provider also exposes `librarian.redis.batch.conflicts` through the `Soenneker.Librarian.Redis` meter.

## Notifications and retention

A transaction publishes its changes to one of 16 feed shards and updates a shared revision. All subscribers to one store share one poll of that revision every 100 milliseconds while idle. A new subscription, a gap or a subscriber buffer overflow produces `Resync`, instructing consumers to refresh from storage. Unchanged-capacity heartbeats and live samples omit dashboard invalidations.

The hosted recorder samples active work every second and sleeps while idle. Empty seconds can be reconstructed only while the lifecycle revision still matches the recorded empty state. Expired samples are excluded from reads and pruned by sampling or maintenance. Retention is implemented through Librarian document deletion rather than Redis key TTLs: stopped workers do not perform cleanup. Completed-job retention defaults to one day; disabling it removes terminal documents on the next maintenance pass. Version markers survive job cleanup.

## Upgrading existing data

Format 5 stores immutable execution payloads separately and reads existing format 4 records. The first committed write upgrades the namespace. Stop or drain format 4 workers before sharing that namespace with the upgraded runtime. Keep a pre-upgrade snapshot if rollback to format 4 is required.
