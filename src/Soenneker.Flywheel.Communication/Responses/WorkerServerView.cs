using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A live worker server discovered from its durable heartbeat.</summary>
/// <param name="Id">Configured Flywheel node identifier.</param>
/// <param name="ExpiresAt">Heartbeat expiration in UTC Unix milliseconds.</param>
/// <param name="Workers">Number of active workers reported by this server.</param>
/// <param name="RunningJobs">Jobs currently leased to this server.</param>
public sealed record WorkerServerView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("expiresAt")] long ExpiresAt,
    [property: JsonPropertyName("workers")] int Workers,
    [property: JsonPropertyName("runningJobs")] IReadOnlyList<Dtos.JobRecord> RunningJobs);
