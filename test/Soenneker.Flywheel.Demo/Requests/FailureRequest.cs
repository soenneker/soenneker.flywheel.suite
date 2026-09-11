using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Demo.Requests;

/// <param name="Reason">Reason included in the simulated failure.</param>
public sealed record FailureRequest(
    [property: JsonPropertyName("reason")] string Reason);
