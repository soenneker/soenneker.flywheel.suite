using Soenneker.Gen.EnumValues;

namespace Soenneker.Flywheel.Core.Enums;

/// <summary>Renewal status; cancellation is durable and separate from lease loss.</summary>
[EnumValue]
public readonly partial struct LeaseStatus
{
    /// <summary>The caller no longer owns a valid lease and must stop processing.</summary>
    public static readonly LeaseStatus Lost = new(0);
    /// <summary>Ownership was extended successfully.</summary>
    public static readonly LeaseStatus Renewed = new(1);
    /// <summary>Cancellation is durable; the owner should stop cooperatively.</summary>
    public static readonly LeaseStatus CancellationRequested = new(2);
}
