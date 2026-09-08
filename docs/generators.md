[![NuGet](https://img.shields.io/nuget/v/Soenneker.Flywheel.Generators.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Generators/)
[![Publish](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/publish-package.yml)
[![Downloads](https://img.shields.io/nuget/dt/Soenneker.Flywheel.Generators.svg?style=for-the-badge)](https://www.nuget.org/packages/Soenneker.Flywheel.Generators/)
[![Build and test](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/build-and-test.yml?label=build%20and%20test&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/build-and-test.yml)
[![CodeQL](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.flywheel.suite/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.flywheel.suite/actions/workflows/codeql.yml)

# Soenneker.Flywheel.Generators

Generates typed job identifiers and dependency injection registrations from `[FlywheelJob]` methods.

## Setup

Add the package to the project containing your job classes:

```shell
dotnet add package Soenneker.Flywheel.Generators
```

Add `PrivateAssets="all"` to its package reference. Follow [Core setup](https://github.com/soenneker/soenneker.flywheel.suite#setup) for the host and storage packages.

## Usage

```csharp
using Soenneker.Flywheel.Core.Attributes;

public sealed record Message(string Text);

public sealed class MessageJobs
{
    [FlywheelJob("message.write.v1")]
    public Task Write(Message message, CancellationToken cancellationToken)
    {
        Console.WriteLine(message.Text);
        return Task.CompletedTask;
    }
}
```

This generates `FlywheelJobs.MessageJobs_Write` and `AddGeneratedJobs()` in `Soenneker.Flywheel.Generated`. See [Core usage](core.md#usage) for registration and enqueueing.

Job classes must be public, top-level, and non-generic. Methods must be public instance methods returning `Task` or `ValueTask`, with a payload followed by `CancellationToken`. Overloads, generic methods, and `ref`/`out` parameters are unsupported.

Keep job names stable and unique. Use `[FlywheelCron]` to declare a cron schedule, then call `RegisterGeneratedSchedules()` on `IJobClient` after building the host.

Distributed method limits can be declared alongside the job and are applied before workers start:

```csharp
[FlywheelJob("message.write.v1", MaxConcurrency = 1, RateLimit = 10, RateWindowSeconds = 60)]
```
