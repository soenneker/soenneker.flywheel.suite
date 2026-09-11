using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Demo.Requests;

/// <param name="Description">Description of the recurring maintenance pulse.</param>
public sealed record PulseRequest(
    [property: JsonPropertyName("description")] string Description);
