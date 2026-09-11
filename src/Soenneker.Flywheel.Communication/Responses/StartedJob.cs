using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Identifier of the execution started from a recurring schedule.</summary>
public sealed record StartedJob(
    [property: JsonPropertyName("id")] string Id);
