using Soenneker.Gen.EnumValues;

namespace Soenneker.Flywheel.Core.Enums;

/// <summary>Dispatch order among eligible jobs; running jobs are never preempted.</summary>
[EnumValue]
public readonly partial struct JobPriority
{
    /// <summary>Lowest dispatch priority among eligible jobs.</summary>
    public static readonly JobPriority Low = new(0);
    /// <summary>Default dispatch priority.</summary>
    public static readonly JobPriority Normal = new(1);
    /// <summary>Preferred over Normal and Low jobs that are also eligible.</summary>
    public static readonly JobPriority High = new(2);
    /// <summary>Highest dispatch priority; does not interrupt running work.</summary>
    public static readonly JobPriority Critical = new(3);
}
