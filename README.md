# Soenneker.Flywheel.Suite

Background jobs, Redis storage, source generators, and a live Blazor dashboard in one solution. Scaffolded with `soenneker.nuget.builder`.

## Packages

Each project produces its own NuGet package. The suite is the repository and solution, not a combined runtime package.

| Package | Responsibility | Flywheel dependencies |
| --- | --- | --- |
| Soenneker.Flywheel.Communication | Shared requests, responses, and typed hub contracts | None |
| Soenneker.Flywheel.Core | Job runtime, dashboard API, and hub | Communication |
| Soenneker.Flywheel.Redis | Redis job storage | Core |
| Soenneker.Flywheel.Generators | Compile-time job registration | None; analyzer only |
| Soenneker.Flywheel.Dashboard | Blazor components, typed consumer, and live client | Communication |

Dashboard does not bring Redis or the server runtime into WebAssembly. Projects reference each other directly in this solution; NuGet packing converts those references into separate package dependencies.

## One demo

Install .NET 10 and run Redis 6.0.9 or newer on localhost:6379, then:

```sh
dotnet dev-certs https --trust
dotnet run --project demo/Soenneker.Flywheel.Demo
```

Open https://localhost:7443/ and sign in with `admin` / `flywheel-demo` in Development. Stop with Ctrl+C. The server hosts the WebAssembly client in `demo/Soenneker.Flywheel.Dashboard.Demo`; both use the source projects from this solution. See [demo configuration](demo/Soenneker.Flywheel.Demo/README.md).

## Development

```sh
dotnet build Soenneker.Flywheel.Suite.slnx
dotnet test --project test/Soenneker.Flywheel.Dashboard.Tests
```

Redis integration tests use `FLYWHEEL_TEST_REDIS`, defaulting to `localhost:16379`. The performance runner is separate from the automated test projects. The product website is included under `website/Soenneker.Flywheel.Website`. Its separate [website workflow](.github/workflows/website.yml) exports and deploys the static site to Cloudflare; see [website development and deployment](website/Soenneker.Flywheel.Website/README.md).

Dashboard pages use Lepton lifecycle management. A typed consumer handles API calls through the Flywheel API client, while a shared live client owns SignalR connections and subscriptions. Shared communication contracts keep the server and dashboard aligned. Dashboard navigation supports `/` and custom home paths, independently of the configured backend address.

## Releases

`build-and-test.yml` builds the entire solution, runs the four test projects with Redis, publishes the hosted demo, and packs five separate packages. `publish-package.yml` calls that validation workflow and then runs a deployment matrix with one job per package, publishing to NuGet and GitHub Packages. A GitHub release is created only after every deployment succeeds.

All packages share `5.0.<publish workflow run number>`. Local builds default to `5.0.0`; `BUILD_VERSION` overrides it. The 5.0 series avoids collisions with independently versioned 4.0 releases and reflects the move of dashboard DTOs into `Soenneker.Flywheel.Communication`. Existing applications must update their response namespace imports accordingly.

## Documentation

- [Core](docs/core.md)
- [Redis](docs/redis.md)
- [Generators](docs/generators.md)
- [Dashboard and authentication](docs/dashboard.md)
