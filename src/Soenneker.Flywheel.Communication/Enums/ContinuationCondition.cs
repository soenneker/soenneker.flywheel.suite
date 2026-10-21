namespace Soenneker.Flywheel.Communication.Enums;

/// <summary>The terminal predecessor outcome required to release dependent work.</summary>
public enum ContinuationCondition
{
    /// <summary>Every predecessor succeeded.</summary>
    OnSuccess,
    /// <summary>Every predecessor finished and at least one exhausted its attempts.</summary>
    OnFailure,
    /// <summary>Every predecessor finished and at least one was explicitly cancelled.</summary>
    OnCancelled,
    /// <summary>Every predecessor finished, regardless of outcome.</summary>
    OnCompleted
}
