using Soenneker.Flywheel.Core.Attributes;
using Soenneker.Flywheel.Demo.Requests;

namespace Soenneker.Flywheel.Demo.Jobs;

public sealed class FailureJobs(ILogger<FailureJobs> logger)
{
    [FlywheelJob("demo.failures.dead-letter.v1")]
    public Task Reject(FailureRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogError("Expected permanent demo failure: {Reason}. This job will exhaust its retry budget.", request.Reason);
        throw new InvalidOperationException("Intentional permanent demo failure");
    }
}
