using Soenneker.Gen.EnumValues;

namespace Soenneker.Flywheel.Core.Enums;

/// <summary>Durable lifecycle states. Running work is owned by a renewable lease.</summary>
[EnumValue]
public readonly partial struct JobState
{
    /// <summary>Eligible to be claimed when its due time and shared limits allow.</summary>
    public static readonly JobState Scheduled = new(0);
    /// <summary>An attempt has been claimed and is owned by a renewable lease.</summary>
    public static readonly JobState Running = new(1);
    /// <summary>The handler completed successfully; this is terminal.</summary>
    public static readonly JobState Succeeded = new(2);
    /// <summary>Execution failed permanently or exhausted its attempt limit; this is terminal.</summary>
    public static readonly JobState DeadLettered = new(3);
    /// <summary>Execution was cancelled; completed external side effects are not reversed.</summary>
    public static readonly JobState Cancelled = new(4);
    /// <summary>A chain step is waiting for its predecessor to succeed.</summary>
    public static readonly JobState Waiting = new(5);
}
