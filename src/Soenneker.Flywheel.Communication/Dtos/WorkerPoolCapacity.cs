using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Dtos;

/// <summary>Configured worker capacity for one case-sensitive pool on a server.</summary>
/// <param name="Name">Pool name.</param>
/// <param name="Workers">Desired concurrent workers, excluding retiring executions.</param>
public sealed record WorkerPoolCapacity(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("workers")] int Workers);
