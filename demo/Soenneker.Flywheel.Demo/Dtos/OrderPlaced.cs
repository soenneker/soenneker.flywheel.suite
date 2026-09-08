namespace Soenneker.Flywheel.Demo.Dtos;

/// <param name="OrderId">Identifier of the order whose event is being handled.</param>
/// <param name="Quantity">Number of items in the order.</param>
/// <param name="Total">Total monetary amount of the order.</param>
public sealed record OrderPlaced(string OrderId, int Quantity, decimal Total);
