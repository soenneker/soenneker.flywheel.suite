using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Demo.Requests;

/// <param name="RequestId">Identifier used to track delivery attempts in the demo.</param>
public sealed record DeliveryRequest(
    [property: JsonPropertyName("requestId")] string RequestId);
