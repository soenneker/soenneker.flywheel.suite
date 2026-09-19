# Flywheel

**Background work. Everything in view.**

Queue, schedule, retry, and chain background jobs in .NET. Flywheel pairs a typed C# job API with your choice of storage and an optional live Blazor dashboard, so you can keep work moving and see what happens along the way.

Use it for report generation, data imports, scheduled maintenance, notifications, and workflows with multiple steps. Start locally with in-memory storage, persist work to disk, or coordinate workers across application instances with Redis.

[**Explore Flywheel →**](https://flywheel.soenneker.com) · [Get started](#get-started) · [Try the dashboard](#try-the-dashboard) · [Documentation](#keep-building)

[![NuGet](https://img.shields.io/nuget/v/Soenneker.Flywheel.Core.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Core/)
[![Build and test](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/build-and-test.yml?label=Build&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/build-and-test.yml)
[![Downloads](https://img.shields.io/nuget/dt/Soenneker.Flywheel.Core.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Core/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=for-the-badge)](LICENSE)

<a href="https://flywheel.soenneker.com">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/dashboard-dark.png" />
    <img src="docs/images/dashboard.png" alt="Flywheel dashboard with live activity, job states, and searchable executions" />
  </picture>
</a>

*The optional Flywheel dashboard, shown with illustrative demo data.*

## Why build with Flywheel?

- **Write the work in C#.** Define a job method, inject your services, and submit a typed payload. Generated job identifiers keep submission connected to your handler.
- **Run on your schedule.** Enqueue immediately, delay execution, choose a specific time, or set up interval and cron schedules with time-zone support.
- **Keep workflows moving.** Chain jobs so each step follows a successful execution. Configure retries, concurrency limits, and rate limits around the work you run.
- **See the execution story.** Follow live activity, search jobs, inspect attempts and progress, and read captured `ILogger` output in the dashboard.
- **Choose the storage that fits.** Use memory for a simple local start, filesystem storage for persistence in a single process, or Redis for shared work across instances.
- **Use AOT-friendly dispatch.** Generated invokers call job methods directly without runtime reflection. Full application AOT compatibility depends on the host, storage, and serialization configuration.

Flywheel targets **.NET 10** and is **MIT licensed**. The job runtime works independently of the dashboard; add the UI when you want an operational view of your workers.

## Put background work to work

| What you need | How Flywheel helps |
| --- | --- |
| **Reports, imports, and notifications** | Submit a job and let background workers execute it while your application continues serving requests. |
| **Nightly cleanup or weekday reports** | Register an interval or cron schedule, including the time zone your business uses. |
| **Workflows with dependent steps** | Chain jobs so later steps wait for earlier steps to succeed, including through retries. |
| **Controlled calls to external services** | Apply job concurrency and rate limits to manage how much work starts at once. |
| **Visibility when a job needs attention** | Find executions, inspect their attempts and logs, follow progress, and cancel work from the dashboard. |

## Get started

Run your first job with **.NET 10**, using in-memory storage so no external database is needed.

### 1. Create an app and install the packages

```shell
dotnet new web -n MyFlywheelApp
cd MyFlywheelApp

dotnet add package Soenneker.Flywheel.Core
dotnet add package Soenneker.Flywheel.Memory
dotnet add package Soenneker.Flywheel.Generators
```

In your `.csproj`, add `PrivateAssets="all"` to the existing `Soenneker.Flywheel.Generators` package reference, keeping its installed version.

### 2. Define and queue a job

Replace `Program.cs` with:

```csharp
using System.Text.Json.Serialization;
using Soenneker.Flywheel.Core.Attributes;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Generated;
using Soenneker.Flywheel.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFlywheel(options => options.Workers = 4)
    .AddMemory()
    .AddGeneratedJobs();

var app = builder.Build();
var client = app.Services.GetRequiredService<IJobClient>();

await client.Enqueue(
    FlywheelJobs.MessageJobs_Write,
    new Message("Hello from Flywheel"));

await app.RunAsync();

public sealed record Message(
    [property: JsonPropertyName("text")] string Text);

public sealed class MessageJobs(ILogger<MessageJobs> logger)
{
    [FlywheelJob("message.write.v1")]
    public Task Write(Message message, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Message}", message.Text);
        return Task.CompletedTask;
    }
}
```

### 3. Run it

```shell
dotnet run
```

Look for **Hello from Flywheel** in the console. Your application queues the message on startup, and a Flywheel worker runs the handler. Press **Ctrl+C** to stop it.

`Enqueue` returns after saving the job to your selected storage; execution happens in the background. In your own application, inject `IJobClient` into the service or endpoint that submits work, and inject application services into your job class.

**Memory storage is temporary:** jobs, schedules, and history are lost when the process stops. Choose filesystem or Redis storage when work must survive restarts.

## One job, your schedule

Using the `client` and `MessageJobs` from the quick start, add these calls before `app.RunAsync()`, or use the same API from an injected `IJobClient`:

```csharp
// Give a job ten minutes before it becomes eligible to run.
await client.Enqueue(FlywheelJobs.MessageJobs_Write,
    new Message("Follow up later"),
    delay: TimeSpan.FromMinutes(10));

// Run once at a specific time.
await client.Schedule(FlywheelJobs.MessageJobs_Write,
    new Message("Tomorrow's reminder"),
    DateTimeOffset.UtcNow.AddDays(1));

// Repeat at a fixed interval.
await client.Recurring("hourly-message", FlywheelJobs.MessageJobs_Write,
    new Message("Hourly check-in"), TimeSpan.FromHours(1));

// Run at 9 a.m. every weekday in Chicago.
await client.Schedule("weekday-message", FlywheelJobs.MessageJobs_Write,
    new Message("Good morning"), "0 9 * * MON-FRI",
    timeZoneId: "America/Chicago");
```

Keep the host running so workers can process scheduled work. Reusing a recurring or cron schedule ID leaves the existing schedule unchanged.

Jobs can execute more than once, so make handlers safe to retry and pass the cancellation token to asynchronous work. See the [job execution guide](docs/core.md) and [client API](src/Soenneker.Flywheel.Core/Services/Abstract/IJobClient.cs) for chains, submission deduplication, progress reporting, and execution policies.

### Keep concurrency under control

Declare limits on the job method when a workload needs them:

```csharp
[FlywheelJob("message.write.v1",
    MaxConcurrency = 1,
    RateLimit = 10,
    RateWindowSeconds = 60)]
```

Applied to the quick start's `Write` method, this allows one execution at a time and up to ten execution starts per minute. With Redis, workers sharing the same job storage coordinate these limits across instances.

## Choose your storage

Choose one provider when registering Flywheel. Your job methods and submission API stay the same.

| Provider | Best fit | What to know |
| --- | --- | --- |
| [Memory](docs/memory.md) | Local development, tests, and temporary background work | No database to run. Each application process has independent data, which is lost on restart. |
| [Filesystem](docs/filesystem.md) | Local applications and modest workloads that need persistence | Stores jobs on disk without a database server. One process owns the database path at a time. |
| [Redis](docs/redis.md) | Shared queues and workers across application instances | Requires Redis. Configure persistence and a no-eviction policy when jobs must survive restarts without data loss. |

To switch the quick start to Redis, install `Soenneker.Flywheel.Redis`, replace the memory namespace with `using Soenneker.Flywheel.Redis;`, and replace its registration with:

```csharp
builder.Services.AddFlywheel(options => options.Workers = 4)
    .AddRedis(options =>
    {
        options.ConnectionString = "localhost:6379";
        options.Namespace = "my-app";
    })
    .AddGeneratedJobs();
```

Workers sharing jobs should use the same storage configuration and job registrations. See the [Redis guide](docs/redis.md) for deployment settings and version requirements.

## Try the dashboard

The optional Blazor dashboard brings the runtime into view:

- **Watch activity live:** see job states, execution activity, and active servers.
- **Find the work that matters:** search jobs and inspect individual executions.
- **Follow every attempt:** view timing, progress, and the logs your handlers write.
- **Manage ongoing work:** inspect schedules, trigger recurring jobs with **Run now**, and cancel jobs.
- **Work in light or dark mode:** follow your system theme or choose your own preference.

<a href="docs/dashboard.md">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/images/execution-dark.png" />
    <img src="docs/images/execution.png" alt="Flywheel execution details showing a report job and its captured logs" />
  </picture>
</a>

*Execution details shown with illustrative demo data.*

### Run the complete demo

From a checkout of this repository, with **.NET 10** installed and **Redis 6.0.9 or newer** running at `localhost:6379`:

```shell
dotnet dev-certs https --trust
dotnet run --project test/Soenneker.Flywheel.Demo
```

In a second terminal:

```shell
dotnet run --project test/Soenneker.Flywheel.Dashboard.Demo
```

Open [the local dashboard](https://localhost:7039/) and sign in with `admin` / `flywheel-demo`. These demo credentials apply in Development when no password hash is configured. Stop both processes with **Ctrl+C**.

The demo seeds sample jobs and recurring schedules so you have work to explore. See [demo configuration](test/Soenneker.Flywheel.Demo/README.md) for connection settings and how to start a fresh tour.

To add the dashboard to your own application, follow the [dashboard guide](docs/dashboard.md) for the Blazor host, backend API, and authentication setup.

## Keep building

Flywheel ships as focused NuGet packages. Most applications start with **Core + one storage provider + Generators**; add **Dashboard** for the UI.

| Package | What it adds |
| --- | --- |
| [Soenneker.Flywheel.Core](https://www.nuget.org/packages/Soenneker.Flywheel.Core/) | Job runtime, client API, and dashboard backend. |
| [Soenneker.Flywheel.Memory](https://www.nuget.org/packages/Soenneker.Flywheel.Memory/) | In-process storage. |
| [Soenneker.Flywheel.Filesystem](https://www.nuget.org/packages/Soenneker.Flywheel.Filesystem/) | Local disk persistence. |
| [Soenneker.Flywheel.Redis](https://www.nuget.org/packages/Soenneker.Flywheel.Redis/) | Shared Redis storage. |
| [Soenneker.Flywheel.Generators](https://www.nuget.org/packages/Soenneker.Flywheel.Generators/) | Typed job identifiers and registrations from your job methods. |
| [Soenneker.Flywheel.Dashboard](https://www.nuget.org/packages/Soenneker.Flywheel.Dashboard/) | Live Blazor dashboard. |

The suite is the repository containing these packages, rather than a combined runtime package. Shared job and dashboard contracts are supplied by `Soenneker.Flywheel.Communication`.

[Job execution and scheduling](docs/core.md) · [Job method requirements](docs/generators.md) · [Dashboard setup](docs/dashboard.md) · [Demo source](test/Soenneker.Flywheel.Demo)

## Open source, ready for your next project

Flywheel is available under the [MIT license](LICENSE). Try it in your application, explore the demo, and help shape what comes next.

Found a bug or have a workflow to suggest? [Open an issue](https://github.com/soenneker/soenneker.flywheel.suite/issues). Contributions and example improvements are welcome. If Flywheel helps you ship, consider starring the repository so other .NET developers can find it.
