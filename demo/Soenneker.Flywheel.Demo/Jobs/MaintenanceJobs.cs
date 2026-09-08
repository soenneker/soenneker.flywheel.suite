using Soenneker.Flywheel.Core.Attributes;
using Soenneker.Flywheel.Demo.Requests;

namespace Soenneker.Flywheel.Demo.Jobs;

public sealed class MaintenanceJobs(ILogger<MaintenanceJobs> logger)
{
    [FlywheelJob("demo.maintenance.recurring.v1")]
    public ValueTask Pulse(PulseRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("{Description}: recurring occurrence executed successfully.", request.Description);
        return ValueTask.CompletedTask;
    }
}
