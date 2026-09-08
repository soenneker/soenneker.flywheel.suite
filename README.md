[![NuGet](https://img.shields.io/nuget/v/Soenneker.Flywheel.Core.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Core/)
[![Publish](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/publish-package.yml)
[![Downloads](https://img.shields.io/nuget/dt/Soenneker.Flywheel.Core.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Core/)
[![Build and test](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/build-and-test.yml?label=build%20and%20test&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/build-and-test.yml)
[![CodeQL](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/codeql.yml)

# Soenneker.Flywheel.Suite

Background jobs, Redis storage, source generators, and a live Blazor dashboard in one solution.

**Background work. Everything in view.**

Queue a typed job, coordinate workers across instances, and follow each execution in the live Blazor dashboard.

[![Flywheel dashboard with live activity, job states, and searchable executions](docs/images/dashboard.png)](https://flywheel.soenneker.com)

*The optional Flywheel Dashboard, shown with illustrative demo data.*

[Explore Flywheel](https://flywheel.soenneker.com) · [Dashboard](docs/dashboard.md) · [Run the demo](test/Soenneker.Flywheel.Demo/README.md)

## Packages

Each project produces its own NuGet package. The suite is the repository and solution, not a combined runtime package.

| Package | Responsibility | Flywheel dependencies |
| --- | --- | --- |
| Soenneker.Flywheel.Communication | Shared job DTOs, policies, enums, requests, responses, and typed hub contracts | No Flywheel dependencies |
| Soenneker.Flywheel.Core | Job runtime, dashboard API, and hub | Communication |
| Soenneker.Flywheel.Redis | Redis job storage | Core |
| Soenneker.Flywheel.Generators | Compile-time job registration | None; analyzer only |
| Soenneker.Flywheel.Dashboard | Blazor components, typed consumer, and live client | Communication |

Dashboard does not bring Redis or the server runtime into WebAssembly. Projects reference each other directly in this solution; NuGet packing converts those references into separate package dependencies.

## One demo

Install .NET 10 and run Redis 6.0.9 or newer on localhost:6379, then:

```sh
dotnet dev-certs https --trust
dotnet run --project test/Soenneker.Flywheel.Demo
```

In a second terminal, run `dotnet run --project test/Soenneker.Flywheel.Dashboard.Demo`. Open https://localhost:7039/ and sign in with `admin` / `flywheel-demo` in Development. The engine/API runs at https://localhost:7443/ and the dashboard runs separately. The solution's `Demo` startup configuration starts both and opens only the dashboard browser. Stop both with Ctrl+C. See [demo configuration](test/Soenneker.Flywheel.Demo/README.md).

## Development

```sh
dotnet build Soenneker.Flywheel.Suite.slnx
dotnet test --project test/Soenneker.Flywheel.Dashboard.Tests
```

Redis integration tests use `FLYWHEEL_TEST_REDIS`, defaulting to `localhost:16379`. Manual [Redis benchmarks](test/Soenneker.Flywheel.Redis.Tests/Benchmarks/README.md) live in the Redis test project and are excluded from normal test runs. The product website is included under `src/Soenneker.Flywheel.Website`. Its separate [website workflow](.github/workflows/website.yml) exports and deploys the static site to Cloudflare; see [website development and deployment](src/Soenneker.Flywheel.Website/README.md).

Dashboard pages use Lepton lifecycle management. A typed consumer handles API calls through the Flywheel API client, while a shared live client owns SignalR connections and subscriptions. Shared communication contracts keep the server and dashboard aligned. Dashboard navigation supports `/` and custom home paths, independently of the configured backend address.

## Releases

`build-and-test.yml` builds the entire solution, runs the four test projects with Redis, publishes the hosted demo, and packs five separate packages. `publish-package.yml` calls that validation workflow and then runs a deployment matrix with one job per package, publishing to NuGet and GitHub Packages. A GitHub release is created only after every deployment succeeds.

All packages share `5.0.<publish workflow run number>`. Local builds default to `5.0.0`; `BUILD_VERSION` overrides it. The 5.0 series avoids collisions with independently versioned 4.0 releases and reflects the move of shared job and dashboard contracts into `Soenneker.Flywheel.Communication`. Update imports for DTOs, enums, requests, responses, and log DTOs from `Soenneker.Flywheel.Core` to `Soenneker.Flywheel.Communication`. The storage server snapshot is now `Communication.Responses.WorkerServerView`; the dashboard response remains `Communication.Responses.ServerView`.

## Documentation

- [Core](docs/core.md)
- [Redis](docs/redis.md)
- [Generators](docs/generators.md)
- [Dashboard and authentication](docs/dashboard.md)
