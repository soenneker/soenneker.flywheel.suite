using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Current public state of a subscribed job.</summary>
public sealed record LiveJob(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("job")] JobView? Job);
