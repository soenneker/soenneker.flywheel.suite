[![NuGet](https://img.shields.io/nuget/v/Soenneker.Flywheel.Core.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Core/)
[![Publish](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/publish-package.yml)
[![Downloads](https://img.shields.io/nuget/dt/Soenneker.Flywheel.Core.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Core/)
[![Build and test](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/build-and-test.yml?label=build%20and%20test&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/build-and-test.yml)
[![CodeQL](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/codeql.yml)

# Soenneker.Flywheel.Suite

Background jobs for .NET with Redis, in-process memory, or filesystem storage, source-generated job registration, and an optional live Blazor dashboard.

<a href="https://flywheel.soenneker.com">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/dashboard-dark.png" />
    <img src="docs/images/dashboard.png" alt="Flywheel dashboard with live activity, job states, and searchable executions" />
  </picture>
</a>

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

## Theme

The dashboard follows the OS light/dark preference until a user toggles the theme. The account menu's **Use system theme** action clears that override. Theme initialization also runs on sign-in, even when no theme toggle is visible. Hosts can apply the theme before Blazor starts by loading Quark's theme initializer in the document head (see the dashboard demo's `wwwroot/index.html`).

Blazor components can inject `Soenneker.Quark.IThemeInterop`, call `Initialize()` after the first interactive render, read `IsDark`, and subscribe to `ThemeChanged` to select light/dark images. Render from event handlers through `InvokeAsync` and unsubscribe on disposal. `UseSystem()` restores OS tracking.

### Coordinated Quark development

The dashboard and website use Quark 4.0.1411 or later for the reactive theme API. To develop coordinated changes against a sibling Quark checkout:

```powershell
dotnet build test/Soenneker.Flywheel.Dashboard.Demo -p:UseLocalQuarkProject=true
```

`LocalQuarkProject` can override the default sibling project path. Normal builds use the published Quark package.
