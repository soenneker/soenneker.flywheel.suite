[![NuGet](https://img.shields.io/nuget/v/Soenneker.Flywheel.Dashboard.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Dashboard/)
[![Publish](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/publish-package.yml)
[![Downloads](https://img.shields.io/nuget/dt/Soenneker.Flywheel.Dashboard.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Dashboard/)
[![Build and test](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/build-and-test.yml?label=build%20and%20test&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/build-and-test.yml)
[![CodeQL](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/codeql.yml)

# Soenneker.Flywheel.Dashboard

Blazor WebAssembly dashboard for Flywheel. Search jobs, view status and live logs, and cancel work.

**From the queue to the full execution story.**

See activity as it happens, find the job that needs attention, and read the logs your handlers already write.

[![Flywheel dashboard showing live job activity and execution states](docs/images/dashboard.png)](https://flywheel.soenneker.com)

### Follow every attempt

Open an execution to inspect its state, attempts, timing, and captured `ILogger` output.

[![Flywheel execution details with a report job and its captured logs](docs/images/execution.png)](https://flywheel.soenneker.com)

*Screenshots use isolated, illustrative demo data.*

[Explore Flywheel](https://flywheel.soenneker.com) · [Run the demo](../demo/Soenneker.Flywheel.Demo/README.md)

## Setup

Start with the [runnable demo](../demo/Soenneker.Flywheel.Demo/README.md).

For your own Blazor WebAssembly app, install `Soenneker.Flywheel.Dashboard` and serve it with the Flywheel API over HTTPS. The [sample server](../demo/Soenneker.Flywheel.Demo/Program.cs) shows authentication and API setup. Configure a dashboard password PHC string on the server.

## Usage

Register the dashboard in the WebAssembly host's `Program.cs`:

```csharp
using Soenneker.Flywheel.Dashboard.Registrars;

builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
});
builder.Services.AddFlywheelDashboardAsScoped();
```

For a separately hosted backend, register its base URL instead. This configures the dashboard HTTP client
and all SignalR connections to use that backend and include browser cookies:

```csharp
builder.Services.AddFlywheelDashboardAsScoped(
    new Uri("https://localhost:7002/"), options => options.HomePath = "/");
```

The backend must allow the dashboard's exact origin with credentialed CORS. Its authentication cookies
must also be valid for the deployment's site relationship; different HTTPS localhost ports are same-site
but cross-origin. This overload replaces the separate host HTTP-client registration shown above.

Use `FlywheelRouter` in the host's `App.razor`. It matches dashboard paths explicitly and renders concrete page and layout components. It does not use assembly scanning, reflected route attributes, `Router`, or `RouteView`.

```razor
@using Soenneker.Flywheel.Dashboard

<FlywheelShell>
    <FlywheelRouter>
        <NotFound Context="path">
            <p role="alert">Page not found.</p>
        </NotFound>
    </FlywheelRouter>
</FlywheelShell>
```

The optional `NotFound` fragment receives the base-relative path without a query string or fragment. Render host pages there using explicit conditions and concrete components. Host pages are not discovered from assemblies; configuring `HomePath = "/"` gives Flywheel ownership of the root. You can also embed `<FlywheelBoard />` in a custom page.

See the [WebAssembly demo](../demo/Soenneker.Flywheel.Dashboard.Demo) for Quark/Tailwind styles and the complete host setup.

## Communication and component lifetime

Dashboard pages use `LeptonCancellable` and call `IFlywheelDashboardConsumer`, which follows the
`Soenneker.Blazor.Consumers.Core` pattern and returns `OperationResult<T>`. The Flywheel API-client
implementation owns cookie credentials, antiforgery tokens, request creation, and backend-origin checks.
It is registered as `IFlywheelApiClient`, so it does not replace a host application's bearer-token API client.

`IFlywheelLiveClient` owns SignalR setup and creates page-owned subscriptions for boards, executions,
and logs. Pages receive typed snapshots and provide their UI callbacks; they do not construct HTTP
requests, hub URLs, or protocol method names. Dispose the subscription when the page is disposed.

HTTP responses and SignalR interfaces live in `Soenneker.Flywheel.Communication`, a dependency-free
contracts package published from the Core repository. Core's snapshot factory maps storage records
to these contracts without exposing payloads or lease credentials. Existing response types moved
from `Soenneker.Flywheel.Dashboard.Responses` to `Soenneker.Flywheel.Communication.Responses`.

## Pages and navigation

| Route | Content |
|---|---|
| `/flywheel` | Activity and job search |
| `/signin` | Sign in, then return to the configured home page |
| `/flywheel/recurring` | Recurring schedules and Run now |
| `/flywheel/scheduled` | Pending jobs and retries |
| `/jobs/{JobId}` | Job details and logs |
| `/flywheel/servers` | Active servers |
| `/flywheel/servers/{ServerId}` | Server details |

The default home path is `/flywheel`. To serve the dashboard at `/`, configure the WebAssembly host:

```csharp
builder.Services.AddFlywheelDashboardAsScoped(options => options.HomePath = "/");
```

Signed-out visitors are redirected to `/signin`; successful sign-in returns to `/`. Signed-in visitors to `/signin` also return home. Sign-out goes directly to `/signin`.

With `FlywheelRouter`, this serves the dashboard at `/` without a host-owned `Index.razor`. Remove an old root dashboard wrapper or redirect page when migrating. With the default `HomePath = "/flywheel"`, `/` is passed to your `NotFound` fragment so you can render your own homepage explicitly.

Remove any server-side `app.MapGet("/", ... Results.Redirect("/flywheel"))` mapping so the existing `MapFallbackToFile("index.html")` serves the root page. Home links and sign-in redirects use the configured home path. The built-in `/flywheel` page, recurring and scheduled pages, job detail routes, and `/flywheel` API and hub endpoints keep their existing paths.

Updates use SignalR. Keep Core, Redis, and Dashboard versions compatible when deploying. Job logs come from `ILogger<T>` and are visible to signed-in dashboard users.

See [Core usage](core.md#usage) to define and enqueue jobs.
