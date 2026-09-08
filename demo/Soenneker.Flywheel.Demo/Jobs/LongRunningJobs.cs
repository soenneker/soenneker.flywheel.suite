using Soenneker.Flywheel.Core.Attributes;
using Soenneker.Flywheel.Demo.Requests;

namespace Soenneker.Flywheel.Demo.Jobs;

public sealed class LongRunningJobs(ILogger<LongRunningJobs> logger)
{
    [FlywheelJob("demo.work.cancel-me.v1")]
    public Task Cancellable(WorkRequest request, CancellationToken cancellationToken) => Work(request, cancellationToken);

    [FlywheelJob("demo.work.timeout.v1")]
    public Task TimeOut(WorkRequest request, CancellationToken cancellationToken) => Work(request, cancellationToken);

    private async Task Work(WorkRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting {Seconds} seconds of work. Observe progress or cancel from the dashboard.", request.Seconds);
        try
        {
            for (var elapsed = 0; elapsed < request.Seconds; elapsed++)
            {
                await Task.Delay(1000, cancellationToken);
                if ((elapsed + 1) % 3 == 0) logger.LogInformation("Progress: {Elapsed}/{Total} seconds.", elapsed + 1, request.Seconds);
            }
            logger.LogInformation("Work completed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Cancellation observed; stopping cooperatively and releasing job resources.");
            throw;
        }
    }
}
