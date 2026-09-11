using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A live Flywheel worker server and its currently leased jobs.</summary>
public sealed record ServerView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("expiresAt")] long ExpiresAt,
    [property: JsonPropertyName("workers")] int Workers,
    [property: JsonPropertyName("runningJobs")] List<JobView> RunningJobs);
