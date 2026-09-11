using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Demo.Requests;

/// <param name="Seconds">Requested work duration in seconds.</param>
public sealed record WorkRequest(
    [property: JsonPropertyName("seconds")] int Seconds);
