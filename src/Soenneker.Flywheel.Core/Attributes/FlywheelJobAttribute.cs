namespace Soenneker.Flywheel.Core.Attributes;

/// <summary>Marks a public instance Task or ValueTask method accepting a payload and CancellationToken.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FlywheelJobAttribute(string name) : Attribute
{
    /// <summary>Stable job name used for registration and persisted work.</summary>
    public string Name { get; } = name;
    /// <summary>Optional human-readable description displayed by dashboard clients.</summary>
    public string? Description { get; set; }
    /// <summary>Maximum simultaneous executions across all servers. Zero uses the unlimited default.</summary>
    public int MaxConcurrency { get; set; }
    /// <summary>Maximum execution starts during the configured rate window. Zero disables rate limiting.</summary>
    public int RateLimit { get; set; }
    /// <summary>Rate-limit window in seconds. Defaults to 60 when RateLimit is configured.</summary>
    public int RateWindowSeconds { get; set; } = 60;
}
