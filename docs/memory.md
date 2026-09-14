# Memory storage

`Soenneker.Flywheel.Memory` uses `Soenneker.Librarian.Memory` for document storage, indexes, and conditional batches without a Redis server. Install the package and select it when registering the runtime:

```csharp
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Memory;

builder.Services.AddFlywheel()
    .AddMemory(options =>
    {
        options.HistoryRetention = TimeSpan.FromDays(1);
        options.RetainCompletedJobs = true;
    });
```

Keep the application's generated job registrations and dashboard setup as usual. Choose one storage provider per runtime.

The provider supports job submission and deduplication, priority dispatch, renewable fenced leases, retries and cancellation, method concurrency and rate limits, application-version jobs, chains, interval and cron schedules, progress, bounded logs, server diagnostics, search, history, and live dashboard notifications. All operations are synchronized within the store; multiple worker threads can safely share it.

Each dependency injection service provider owns one `MemoryJobStore`. Separate service providers and application processes have independent jobs, schedules, limits, and version-submission markers. All data is lost on restart. Use Redis when work must survive restarts or coordinate across application instances.

Retention defaults to one day and can range from five minutes to 365 days. Flywheel maintenance removes completed jobs and expired history. Setting `RetainCompletedJobs` to `false` removes terminal records on the next maintenance pass. Aggregate history remains available for the retention period. Job logs retain at most 1,000 entries per job. Live concurrency samples are recorded by a hosted service independently of dashboard connections.

Version-submission markers remain for the lifetime of the store even after completed-job cleanup. Ordinary idempotency keys expire with their retained jobs. Pending jobs and registered schedules remain until executed or the store is discarded; memory usage therefore depends on submitted work as well as the retention settings.

For direct use, construct `MemoryJobStore` with `FlywheelMemoryOptions`. An optional `TimeProvider` controls storage time, allowing deterministic tests without waiting for leases or schedules to expire.

The shared lifecycle implementation lives in Flywheel.Core and uses Librarian abstractions; each existing provider references its corresponding Librarian package. Memory batches stage changes and publish them atomically. Explicit indexes are rebuilt for changed containers, so mutation costs grow with container size. Notifications use the same committed revision feed as the other providers.
