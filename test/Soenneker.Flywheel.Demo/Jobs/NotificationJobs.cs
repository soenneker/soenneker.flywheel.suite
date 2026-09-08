using Soenneker.Flywheel.Core.Attributes;
using Soenneker.Flywheel.Demo.Requests;

namespace Soenneker.Flywheel.Demo.Jobs;

public sealed class NotificationJobs(ILogger<NotificationJobs> logger)
{
    [FlywheelJob("demo.notifications.welcome.v1")]
    public async Task Welcome(WelcomeEmail email, CancellationToken cancellationToken)
    {
        logger.LogInformation("Preparing a simulated welcome email for {Name}.", email.Name);
        await Task.Delay(750, cancellationToken);
        logger.LogInformation("Welcome email simulation completed. No real email was sent.");
    }
}
