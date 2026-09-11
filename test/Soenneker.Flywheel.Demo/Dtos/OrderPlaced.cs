using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Demo.Dtos;

/// <param name="OrderId">Identifier of the order whose event is being handled.</param>
/// <param name="Quantity">Number of items in the order.</param>
/// <param name="Total">Total monetary amount of the order.</param>
public sealed record OrderPlaced(
    [property: JsonPropertyName("orderId")] string OrderId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("total")] decimal Total);
