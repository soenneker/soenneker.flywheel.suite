using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Dtos;

/// <summary>Shared limits for a stable job method across all workers in a storage namespace.</summary>
public sealed record MethodPolicy
{
    /// <summary>Maximum simultaneous valid leases per non-null JobPolicy.PartitionKey within this method, across all workers.
    /// Null disables the partition limit. The method-wide limit and rate limit still apply.
    /// Upgrade all workers sharing the namespace before submitting partitioned work; older workers do not enforce this limit.</summary>
    [JsonPropertyName("maxConcurrencyPerPartition")]
    public int? MaxConcurrencyPerPartition { get; init; }

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
        if (MaxConcurrencyPerPartition is < 1 || MaxConcurrency is < 1 || RateLimit is < 1 || RateWindow < TimeSpan.FromMilliseconds(1) || RateWindow > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(MethodPolicy));
    }
}
