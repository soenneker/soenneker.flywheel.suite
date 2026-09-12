[![NuGet](https://img.shields.io/nuget/v/Soenneker.Flywheel.Redis.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Redis/)
[![Publish](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/publish-package.yml)
[![Downloads](https://img.shields.io/nuget/dt/Soenneker.Flywheel.Redis.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Redis/)
[![Build and test](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/build-and-test.yml?label=build%20and%20test&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/build-and-test.yml)
[![CodeQL](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/codeql.yml)

# Soenneker.Flywheel.Redis

Redis storage for Flywheel jobs, schedules, and logs. Coordinates workers across app instances.

## Setup

Requires Redis 6.0.9 or newer. Follow [Core setup](https://github.com/soenneker/soenneker.flywheel.suite#setup) to install the packages.

## Usage

```csharp
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Generated;
using Soenneker.Flywheel.Redis;

builder.Services.AddFlywheel()
    .AddRedis(options =>
    {
        options.ConnectionString = "localhost:6379";
        options.Namespace = "my-app";
        options.HistoryRetention = TimeSpan.FromDays(30);
        options.RetainCompletedJobs = true;
    })
    .AddGeneratedJobs();
```

Workers sharing jobs must use the same namespace and job registrations. Use separate namespaces for separate apps or environments.

Enable Redis persistence and use `maxmemory-policy noeviction` to prevent job data eviction. The Redis account also needs `PUBLISH`, `SUBSCRIBE`, and `UNSUBSCRIBE` for dashboard updates.

`HistoryRetention` defaults to one day. `RetainCompletedJobs` defaults to `true`; disable it to keep aggregate activity without completed job records after maintenance.

See [Core usage](core.md#usage) to define and enqueue jobs.

## JSON storage contracts

Flywheel's structured Redis records use explicit camelCase `JsonPropertyName` attributes and `Soenneker.Utils.Json` for serialization and deserialization. This includes jobs, schedules, method policies, rate windows, stored semaphore permits, and change notifications. Redis counters, indexes, and log stream fields retain their native representations. Job state and priority retain their numeric JSON values.

## Dispatch index

Dispatch uses a private binary metadata hash containing each scheduled or running job's name, state,
priority, due time, and application version. Selection reads up to four batches of 128 compact entries
at once; it fetches the full JSON record only for the selected job. Concurrency counts use the same
metadata, so large payloads are not downloaded to count running jobs. Terminal transitions and retention
remove metadata. The job JSON format and lease-fencing protocol are unchanged.

The binary index is required and is maintained atomically with the job record. Dispatch uses the
lifecycle revision to fence selection and ownership; there is no separate compatibility checkpoint,
JSON metadata fallback, or automatic index reconstruction. A missing entry in an unchanged snapshot
raises an error. Concurrent terminal transitions can remove entries, and the revision fence prevents
a claim from committing against that outdated snapshot.

All workers sharing a namespace must run the current storage implementation. Pre-index data and
mixed older writers are unsupported; start with a fresh namespace when adopting this storage format.
Live gauges are read only from the current sample ring, and absent or invalid samples remain unknown.
Custom `IJobExecutor` implementations must implement the callback overload and signal immediately after
claiming, before handler execution.

The index adds Redis memory proportional to scheduled and running jobs. Selection still scans all due
metadata to preserve global priority and exact application-version matching; it is not constant-time in
the number of eligible jobs. See the [performance harness](../test/Soenneker.Flywheel.Performance/README.md)
for measured workloads and remaining limits.
