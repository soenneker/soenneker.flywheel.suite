using System.Text.Json.Serialization.Metadata;
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
    /// <param name="typeInfo">Source-generated JSON metadata and serialization options for the value.</param>
    /// <param name="payload">The job payload.</param>
    /// <param name="policy">Optional execution policy.</param>
    /// <param name="delay">Optional delay before this step becomes eligible.</param>
    public JobStep With(T payload, JsonTypeInfo<T> typeInfo, JobPolicy? policy = null, TimeSpan? delay = null) =>
        new(new EnqueueRequest(Name, JsonUtil.Serialize(payload, typeInfo) ?? "null", policy ?? new JobPolicy(), delay ?? TimeSpan.Zero,
            Description: Description));
}
