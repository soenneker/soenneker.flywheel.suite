[![NuGet](https://img.shields.io/nuget/v/Soenneker.Flywheel.Core.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Core/)
[![Publish](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/publish-package.yml)
[![Downloads](https://img.shields.io/nuget/dt/Soenneker.Flywheel.Core.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Core/)
[![Build and test](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/build-and-test.yml?label=build%20and%20test&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/build-and-test.yml)
[![CodeQL](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/codeql.yml)

# Soenneker.Flywheel.Suite

Background jobs for .NET with Redis, in-process memory, or filesystem storage, source-generated job registration, and an optional live Blazor dashboard.

[![Flywheel dashboard with live activity, job states, and searchable executions](docs/images/dashboard.png)](https://flywheel.soenneker.com)

*Dashboard shown with illustrative demo data.*

[Explore Flywheel](https://flywheel.soenneker.com) · [Run the demo](test/Soenneker.Flywheel.Demo/README.md)

## Packages

Install the packages you need; the suite is not a combined runtime package.

| Package | Purpose |
| --- | --- |
| Soenneker.Flywheel.Core | Job runtime, dashboard API, and hub |
| Soenneker.Flywheel.Redis | Redis job storage |
| Soenneker.Flywheel.Memory | In-process job storage |
| Soenneker.Flywheel.Filesystem | Filesystem job storage |
| Soenneker.Flywheel.Generators | Compile-time job registration |
| Soenneker.Flywheel.Dashboard | Blazor dashboard and live client |
| Soenneker.Flywheel.Communication | Shared job and dashboard contracts |

## Run the demo

Install .NET 10 and run Redis 6.0.9 or newer on `localhost:6379`, then:

```sh
dotnet dev-certs https --trust
dotnet run --project test/Soenneker.Flywheel.Demo
```

In a second terminal:

```sh
dotnet run --project test/Soenneker.Flywheel.Dashboard.Demo
```

Open https://localhost:7039/ and sign in with `admin` / `flywheel-demo` in Development. Stop both processes with Ctrl+C. See [demo configuration](test/Soenneker.Flywheel.Demo/README.md) for details.

## Documentation

- [Core and job execution](docs/core.md)
- [Redis storage](docs/redis.md)
- [Memory storage](docs/memory.md)
- [Filesystem storage](docs/filesystem.md)
- [Job registration generators](docs/generators.md)
- [Dashboard and authentication](docs/dashboard.md)
