# PostgreSQL storage

`Soenneker.Flywheel.Postgres` uses `Soenneker.Librarian.Postgres` for persistent jobs, schedules, leases, logs, progress, and dashboard history. Install the package and register it with the Flywheel runtime:

```csharp
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Postgres;

builder.Services.AddFlywheel().AddPostgres(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("Flywheel")!;
    options.Namespace = "my-app";
    options.HistoryRetention = TimeSpan.FromDays(30);
    options.RetainCompletedJobs = true;
});
```

The connection string is required. `Namespace` defaults to `flywheel` and becomes the logical Librarian database key. Workers sharing jobs must use the same PostgreSQL database, namespace, storage version, and job registrations. Different namespaces isolate deployments within the same database.

The store owns its Npgsql connection pool and disposes it with the service provider. Connections open on first use. Librarian automatically creates its `public.librarian_postgres_*` tables and indexes, so the database role needs permission to create these objects as well as read and write them. Provision the PostgreSQL database before starting workers.

## Coordination and storage

Flywheel reuses the shared Librarian job store and document format 4. Conditional batches atomically update jobs, leases, dispatch indexes, rate usage, chains, history, and notifications. Revision checks coordinate independent workers; conflicting operations retry with bounded jitter. PostgreSQL server time drives scheduling and lease expiration, avoiding differences between worker clocks.

Documents remain in PostgreSQL; the provider has no local database mirror or periodic save. Librarian indexes select candidates and matching records. Flywheel performs priority selection, free-text matching, and aggregate shaping over those results.

The change feed polls stored notification revisions and emits `Resync` when subscribing or detecting a revision gap. Consumers refresh authoritative state after a resync. The hosted activity recorder samples active work every second and waits up to 15 seconds while idle, waking on job changes.

History retention defaults to one day and accepts five minutes through 365 days. Disabling `RetainCompletedJobs` removes terminal jobs during maintenance. Cleanup requires running workers; version markers survive completed-job cleanup.

## Integration tests

Set `FLYWHEEL_TEST_POSTGRES` to a connection string for a test database, then run:

```sh
dotnet test --project test/Soenneker.Flywheel.Postgres.Tests -- --treenode-filter "/*/*/PostgresJobStoreTests/*"
```

Integration tests use unique logical namespaces and delete their data afterward. Without the environment variable, database tests are skipped while registration and validation tests still run. CI provisions PostgreSQL and runs all provider tests.
