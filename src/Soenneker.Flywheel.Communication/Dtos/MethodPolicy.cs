using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Dtos;

/// <summary>Shared limits for a stable job method across all workers in a storage namespace.</summary>
public sealed record MethodPolicy
{
    /// <summary>Maximum simultaneous valid leases; null means unlimited. Handlers must observe lease-loss cancellation.</summary>
    [JsonPropertyName("maxConcurrency")]
    public int? MaxConcurrency { get; init; }
    /// <summary>Maximum attempt starts per fixed window; null disables throttling. Retries consume starts too.</summary>
    [JsonPropertyName("rateLimit")]
    public int? RateLimit { get; init; }
    /// <summary>Storage-clock window anchored at the first start; changes reset the window.</summary>
    [JsonPropertyName("rateWindow")]
    public TimeSpan RateWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Rejects nonpositive limits or a rate window outside one millisecond through 365 days.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A configured limit or rate window is invalid.</exception>
    public void Validate()
    {
        if (MaxConcurrency is < 1 || RateLimit is < 1 || RateWindow < TimeSpan.FromMilliseconds(1) || RateWindow > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(MethodPolicy));
    }
}
