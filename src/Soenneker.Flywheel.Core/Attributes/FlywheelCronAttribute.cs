namespace Soenneker.Flywheel.Core.Attributes;

/// <summary>Declares a cron schedule for a FlywheelJob method. Register with the generated RegisterGeneratedSchedules method.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FlywheelCronAttribute(string expression) : Attribute
{
    /// <summary>Five-field cron expression, or six fields when IncludeSeconds is true.</summary>
    public string Expression { get; } = expression;
    /// <summary>Stable schedule ID. Defaults to the FlywheelJob name.</summary>
    public string? Id { get; set; }
    /// <summary>Time zone ID available on every worker. Defaults to UTC.</summary>
    public string TimeZoneId { get; set; } = "UTC";
    /// <summary>JSON payload deserialized to the method's payload type during schedule registration.</summary>
    public string PayloadJson { get; set; } = "{}";
    /// <summary>Whether the expression includes a leading seconds field.</summary>
    public bool IncludeSeconds { get; set; }
}
