using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Communication.Dtos;

/// <summary>A typed, versioned job identifier emitted by the generator.</summary>
/// <param name="Name">Stable job name used for registration and persisted work.</param>
/// <param name="Description">Optional human-readable description displayed by dashboard clients.</param>
public sealed record JobDefinition<T>(string Name, string? Description = null)
{
    /// <summary>Prepares a typed chain step. Delay starts at submission for the first step, or after predecessor success for later steps.</summary>
    public JobStep With(T payload, JobPolicy? policy = null, TimeSpan? delay = null) =>
        new(new EnqueueRequest(Name, System.Text.Json.JsonSerializer.Serialize(payload), policy ?? new JobPolicy(), delay ?? TimeSpan.Zero,
            Description: Description));
}
