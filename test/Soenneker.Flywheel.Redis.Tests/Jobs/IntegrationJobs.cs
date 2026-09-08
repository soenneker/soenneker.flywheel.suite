using Soenneker.Flywheel.Core.Attributes;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Flywheel.Core;
using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Generated;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed class IntegrationJobs(InvocationState state, ILogger<IntegrationJobs> logger) : IDisposable
{
    [FlywheelJob("integration.generated.v1")]
    [FlywheelCron("0 9 * * *", Id = "generated-daily", TimeZoneId = "America/Chicago", PayloadJson = "{\"Value\":\"cron payload\"}")]
    public Task Run(TestPayload payload, CancellationToken cancellationToken)
    { state.Value = payload.Value; logger.LogInformation("Delivered {Value}", payload.Value); return Task.CompletedTask; }
    public void Dispose() => state.Disposed = true;
}
