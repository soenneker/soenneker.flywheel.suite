using Soenneker.Flywheel.Core.Attributes;
using Soenneker.Flywheel.Demo.Dtos;

namespace Soenneker.Flywheel.Demo.Jobs;

public sealed class OrderJobs(ILogger<OrderJobs> logger)
{
    [FlywheelJob("demo.events.order-placed.v1")]
    public async Task OnPlaced(OrderPlaced order, CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing order event {OrderId}: {Quantity} items, total {Total}.", order.OrderId, order.Quantity, order.Total);
        await Task.Delay(1000, cancellationToken);
        logger.LogInformation("Inventory reservation and receipt simulated. Real event handlers should deduplicate by OrderId.");
    }
}
