using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Core.Services.Abstract;

/// <summary>Storage-specific coordination for debounced jobs. Register a scoped or singleton implementation to enable the JobClient debounce API.</summary>
public interface IJobDebounceCoordinator
{
    /// <summary>Persists the newest request and cancels its predecessor. Duplicate and older requests must not replace newer work.</summary>
    Task Enqueue(string key, string requestId, DateTimeOffset requestedAt, EnqueueRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the latest accepted request identifier.</summary>
    ValueTask<string?> Current(string key, CancellationToken cancellationToken = default);

    /// <summary>Runs a short, idempotent external commit only while the request remains current, excluding replacement submissions until completion.</summary>
    ValueTask<bool> Commit(string key, string? requestId, Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default);
}
