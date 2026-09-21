using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Core.Services.Abstract;

/// <summary>Atomic debounce operations implemented by a Flywheel job store.</summary>
public interface IJobDebounceCoordinator
{
    /// <summary>Persists the newest request and cancels its predecessor. Duplicate and older requests must not replace newer work.</summary>
    Task Enqueue(string key, string requestId, DateTimeOffset? requestedAt, EnqueueRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the latest accepted request identifier.</summary>
    ValueTask<string?> Current(string key, CancellationToken cancellationToken = default);

    /// <summary>Runs a short, idempotent external commit only while the request remains current, excluding replacement submissions until completion.
    /// Callbacks must observe cancellation and finish within four minutes; the coordination lease expires after five minutes for crash recovery.</summary>
    ValueTask<bool> Commit(string key, string? requestId, Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default);
}
