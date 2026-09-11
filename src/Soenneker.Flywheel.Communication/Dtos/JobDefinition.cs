using System.Text.Json.Serialization;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Utils.Json;

namespace Soenneker.Flywheel.Communication.Dtos;

/// <summary>A typed, versioned job identifier emitted by the generator.</summary>
/// <param name="Name">Stable job name used for registration and persisted work.</param>
/// <param name="Description">Optional human-readable description displayed by dashboard clients.</param>
public sealed record JobDefinition<T>(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null)
{
    /// <summary>Prepares a typed chain step. Delay starts at submission for the first step, or after predecessor success for later steps.</summary>
    public JobStep With(T payload, JobPolicy? policy = null, TimeSpan? delay = null) =>
        new(new EnqueueRequest(Name, JsonUtil.Serialize(payload) ?? "null", policy ?? new JobPolicy(), delay ?? TimeSpan.Zero,
            Description: Description));
}
