using Soenneker.Flywheel.Core.Attributes;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed class IntegrationJobs(InvocationState state, ILogger<IntegrationJobs> logger) : IDisposable
{
    [FlywheelJob("integration.generated.v1")]
    [FlywheelCron("0 9 * * *", Id = "generated-daily", TimeZoneId = "America/Chicago", PayloadJson = "{\"Value\":\"cron payload\"}")]
    public Task Run(TestPayload payload, CancellationToken cancellationToken)
    { state.Value = payload.Value; logger.LogInformation("Delivered {Value}", payload.Value); return Task.CompletedTask; }
    public void Dispose() => state.Disposed = true;
}
