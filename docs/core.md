[![NuGet](https://img.shields.io/nuget/v/Soenneker.Flywheel.Core.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Core/)
[![Publish](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/publish-package.yml)
[![Downloads](https://img.shields.io/nuget/dt/Soenneker.Flywheel.Core.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Core/)
[![Build and test](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/build-and-test.yml?label=build%20and%20test&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/build-and-test.yml)
[![CodeQL](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/codeql.yml)

# Soenneker.Flywheel.Core

Background jobs for .NET with Redis storage, retries, scheduling, and job chains.

**Background work. Everything in view.**

Queue a typed job, coordinate workers across instances, and follow each execution in the live Blazor dashboard.

[![Flywheel dashboard with live activity, job states, and searchable executions](docs/images/dashboard.png)](https://flywheel.soenneker.com)

*The optional Flywheel Dashboard, shown with illustrative demo data.*

[Explore Flywheel](https://flywheel.soenneker.com) · [Dashboard](https://github.com/soenneker/soenneker.flywheel.suite) · [Run the demo](https://github.com/soenneker/soenneker.flywheel.suite/blob/main/demo/Soenneker.Flywheel.Demo/README.md)

## Setup

Requires .NET 10 and Redis 6.0.9 or newer. Add these packages to your app:

```shell
dotnet add package Soenneker.Flywheel.Core
dotnet add package Soenneker.Flywheel.Redis
dotnet add package Soenneker.Flywheel.Generators
```

Add `PrivateAssets="all"` to the Generators package reference in your project file.

## Usage

In an ASP.NET Core app's `Program.cs`, register Flywheel and enqueue a job:

```csharp
using Soenneker.Flywheel.Core.Attributes;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Generated;
using Soenneker.Flywheel.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFlywheel(options => options.Workers = 4)
    .AddRedis(options => options.ConnectionString = "localhost:6379")
    .AddGeneratedJobs();

var app = builder.Build();
var client = app.Services.GetRequiredService<IJobClient>();

await client.Enqueue(FlywheelJobs.MessageJobs_Write,
    new Message("Hello from Flywheel"));

await app.RunAsync();

public sealed record Message(string Text);

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

When hosting the optional dashboard API, register its credentials with `AddDashboard`, map controllers for its HTTP endpoints, and let Flywheel map its authenticated real-time endpoint:

```csharp
builder.Services.AddFlywheel()
    .AddRedis(options => options.ConnectionString = "localhost:6379")
    .AddDashboard(options =>
    {
        options.Username = builder.Configuration["Flywheel:Dashboard:Username"] ?? "";
        options.PasswordPhc = builder.Configuration["Flywheel:Dashboard:PasswordPhc"] ?? "";
        options.AllowedOrigins = ["https://localhost:7004"];
    });

var app = builder.Build();
app.UseRouting();
app.UseFlywheelDashboard();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapFlywheelDashboard();
```

`AllowedOrigins` contains additional browser origins that may access the dashboard,
including their scheme and port. An empty list allows same-origin browsers only.
Wildcards, credentials, paths, queries, and fragments are rejected at registration.
Call `UseFlywheelDashboard()` after routing and trusted forwarded-header processing,
before authentication and authorization. It applies credentialed CORS and rejects
unlisted `Origin` headers with HTTP 403, including SignalR WebSocket handshakes;
[CORS alone does not restrict WebSockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0#websocket-origin-restriction).
Other application endpoints are unaffected.

This controls browser origins, not client IP addresses. Requests without `Origin`
remain subject to dashboard authentication and antiforgery checks. Keep authentication
enabled; use network access controls if the server itself must be unreachable from
other clients. Dashboard cookies remain Secure and SameSite=Strict, so direct cookie
authentication requires HTTPS and same-site frontend/backend addresses (different
localhost ports or subdomains of the same site work).

`Enqueue` returns after saving the job. Workers run it in the background. In other services, inject `IJobClient` to submit jobs.

Handlers can inject `IJobProgress` and publish durable progress while they run:

```csharp
public sealed class ImportJobs(IJobProgress progress)
{
    [FlywheelJob("contacts.import.v1")]
    public async Task Import(ImportRequest request, CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Contacts.Count; i++)
        {
            await Import(request.Contacts[i], cancellationToken);
            await progress.Report((i + 1d) / request.Contacts.Count * 100, $"Imported {i + 1} contacts", cancellationToken);
        }
    }
}
```

Declare distributed execution limits with the job so every server applies the same generated policy before workers start:

```csharp
[FlywheelJob("contacts.import.v1", MaxConcurrency = 1)]
public Task Import(ImportRequest request, CancellationToken cancellationToken) => // ...
```

`IJobClient.ConfigureMethod` remains available for limits that genuinely need to change at runtime.

## Queue and schedule jobs

Use `IJobClient` for immediate, delayed, scheduled, recurring, cron, or chained jobs:

```csharp
await client.Enqueue(FlywheelJobs.MessageJobs_Write, new Message("Later"),
    delay: TimeSpan.FromMinutes(10));

await client.Recurring("hourly-message", FlywheelJobs.MessageJobs_Write,
    new Message("Every hour"), TimeSpan.FromHours(1));

await client.Schedule("weekday-message", FlywheelJobs.MessageJobs_Write,
    new Message("Good morning"), "0 9 * * MON-FRI",
    timeZoneId: "America/Chicago");
```

See [IJobClient](src/Soenneker.Flywheel.Core/Services/Abstract/IJobClient.cs) for the full API.

Jobs can run more than once; make handlers safe to retry and pass cancellation tokens to async work. Keep job names stable. Reusing a recurring or cron schedule ID leaves the existing schedule unchanged.

## Related

- [Redis](https://github.com/soenneker/soenneker.flywheel.suite): storage configuration.
- [Generators](https://github.com/soenneker/soenneker.flywheel.suite): job method requirements.
- [Dashboard](https://github.com/soenneker/soenneker.flywheel.suite): view jobs and logs.
- [Demo](https://github.com/soenneker/soenneker.flywheel.suite/blob/main/demo/Soenneker.Flywheel.Demo/README.md): run a complete app.
