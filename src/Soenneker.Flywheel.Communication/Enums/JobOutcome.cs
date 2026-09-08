using Soenneker.Gen.EnumValues;

namespace Soenneker.Flywheel.Communication.Enums;

/// <summary>Worker outcome, committed only by a current owner.</summary>
[EnumValue]
public readonly partial struct JobOutcome
{
    /// <summary>The handler completed successfully.</summary>
    public static readonly JobOutcome Succeeded = new(0);
    /// <summary>The attempt failed; the store applies retry policy or dead-letters the job.</summary>
    public static readonly JobOutcome Failed = new(1);
    /// <summary>The attempt stopped in response to cancellation.</summary>
    public static readonly JobOutcome Cancelled = new(2);
}
