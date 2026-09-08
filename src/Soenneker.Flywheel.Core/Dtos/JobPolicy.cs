namespace Soenneker.Flywheel.Core.Dtos;

/// <summary>Persisted execution policy; attempt limits include recovered executions.</summary>
public sealed record JobPolicy
{
    /// <summary>Dispatch priority among due jobs. Defaults to Normal.</summary>
    public Enums.JobPriority Priority { get; init; } = Enums.JobPriority.Normal;

    /// <summary>Maximum execution attempts, including attempts recovered after a lost lease.</summary>
    public int MaxAttempts { get; init; } = 5;
    /// <summary>Maximum duration of one execution attempt.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Base delay before retrying the first failed attempt.</summary>
    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Maximum retry delay after exponential backoff and jitter are applied.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromHours(1);
    /// <summary>Fractional random variation applied to the retry delay, from zero through one.</summary>
    public double Jitter { get; init; } = .2;

    /// <summary>Validates priority, attempt limits, timeout, backoff bounds, and finite jitter between zero and one.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An execution or retry setting is outside its supported range.</exception>
    public void Validate()
    {
        if (!Enums.JobPriority.IsDefined(Priority.Value) || MaxAttempts is < 1 or > 1000 || Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromDays(1) ||
            InitialBackoff <= TimeSpan.Zero || MaxBackoff < InitialBackoff || MaxBackoff > TimeSpan.FromDays(30) ||
            !double.IsFinite(Jitter) || Jitter is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(JobPolicy));
    }

    /// <summary>Calculates capped exponential backoff with jitter for a failed attempt.</summary>
    /// <param name="attempt">One-based attempt number that just failed.</param>
    /// <param name="random">Finite random sample between zero and one, inclusive.</param>
    /// <returns>A retry delay no greater than MaxBackoff.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The policy, attempt, or random sample is invalid.</exception>
    public TimeSpan RetryDelay(int attempt, double random)
    {
        Validate();
        if (attempt < 1 || !double.IsFinite(random) || random is < 0 or > 1) throw new ArgumentOutOfRangeException();
        return TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds,
            InitialBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30)) * (1 - Jitter + 2 * Jitter * random)));
    }
}
