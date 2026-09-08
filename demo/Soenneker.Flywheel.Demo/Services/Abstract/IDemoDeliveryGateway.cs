namespace Soenneker.Flywheel.Demo.Services.Abstract;

/// <summary>A simulated downstream service that durably fails its first two delivery calls.</summary>
public interface IDemoDeliveryGateway
{
    /// <summary>Records a call in Redis; throws a simulated transient error for the first two calls. Sends nothing externally.</summary>
    Task Deliver(string requestId, CancellationToken cancellationToken);
}
