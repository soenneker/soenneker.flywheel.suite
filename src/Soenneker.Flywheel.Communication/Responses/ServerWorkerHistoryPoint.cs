using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A server's busy-worker count observed at a heartbeat.</summary>
/// <param name="Timestamp">Observation time in UTC Unix milliseconds.</param>
/// <param name="BusyWorkers">Number of jobs with a live lease on the server.</param>
/// <param name="ExpiresAt">Time after which this observation is no longer considered live.</param>
public sealed record ServerWorkerHistoryPoint(
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("busyWorkers")] int BusyWorkers,
    [property: JsonPropertyName("expiresAt")] long ExpiresAt);
