namespace Soenneker.Flywheel.Communication.Enums;

/// <summary>An operator action on a pending execution.</summary>
public enum JobQueueAction
{
    /// <summary>Makes a scheduled execution eligible now, without bypassing execution limits.</summary>
    RunNow,
    /// <summary>Moves an eligible pending execution ahead of other pending work.</summary>
    MoveToTop,
    /// <summary>Moves an eligible pending execution behind existing pending work.</summary>
    MoveToBottom
}
