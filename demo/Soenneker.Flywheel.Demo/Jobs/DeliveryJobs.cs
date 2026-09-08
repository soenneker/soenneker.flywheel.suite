using Soenneker.Flywheel.Core.Attributes;
using Soenneker.Flywheel.Demo.Requests;
using Soenneker.Flywheel.Demo.Services.Abstract;

namespace Soenneker.Flywheel.Demo.Jobs;

public sealed class DeliveryJobs(IDemoDeliveryGateway gateway)
{
    [FlywheelJob("demo.delivery.retry.v1")]
    public Task Send(DeliveryRequest request, CancellationToken cancellationToken) => gateway.Deliver(request.RequestId, cancellationToken);
}
