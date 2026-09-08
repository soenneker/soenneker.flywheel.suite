namespace Soenneker.Flywheel.Core.Services.Abstract;

/// <summary>Reports durable progress for the currently executing job. Resolve this service in a job handler's execution scope.</summary>
public interface IJobProgress
{
    /// <summary>Persists a percentage from 0 through 100 and an optional status message for the current attempt.</summary>
    ValueTask Report(double percentage, string? message = null, CancellationToken cancellationToken = default);
}
