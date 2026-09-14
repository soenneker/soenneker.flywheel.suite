using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Identifier of the execution queued by a manual run action.</summary>
public sealed record StartedJob(
    [property: JsonPropertyName("id")] string Id);
