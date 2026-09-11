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
