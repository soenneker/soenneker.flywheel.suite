using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Dtos;

/// <summary>A committed job, schedule or log change; Resync invalidates all subscribed snapshots.</summary>
public sealed record JobChange(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("jobId")] string? JobId = null)
{
    /// <summary>Requests fresh snapshots after notifications may have been lost, such as a reconnect.</summary>
    public static readonly JobChange Resync = new("Resync");
}
