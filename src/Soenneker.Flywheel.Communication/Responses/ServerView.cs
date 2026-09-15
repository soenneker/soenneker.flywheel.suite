using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A live Flywheel worker server and its currently leased jobs.</summary>
public sealed record ServerView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("expiresAt")] long ExpiresAt,
    [property: JsonPropertyName("workers")] int Workers,
    [property: JsonPropertyName("runningJobs")] List<JobView> RunningJobs)
{
    /// <summary>Recent worker counts recorded by server heartbeats, ordered oldest first.</summary>
    [JsonPropertyName("workerHistory")]
    public IReadOnlyList<ServerWorkerHistoryPoint> WorkerHistory { get; init; } = [];

    /// <summary>Server-side UTC Unix milliseconds at which this snapshot was read.</summary>
    [JsonPropertyName("observedAt")]
    public long ObservedAt { get; init; }
}