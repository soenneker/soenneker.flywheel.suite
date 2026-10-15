using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Atomically changes dispatch order or eligibility of a pending execution.</summary>
public interface IJobQueueStore
{
    /// <summary>Updates an existing pending execution without creating a duplicate or consuming an attempt.
    /// Top and bottom require due work; RunNow releases a scheduled execution's delay.
    /// Pool, version, instance, concurrency, and rate restrictions still apply. Running, waiting, cancelled,
    /// and finished jobs cannot be reordered. Returns false if missing or no longer eligible for the action.
    /// Queue overrides apply to the next attempt only; retries rejoin ordinary dispatch order.
    /// Upgrade all workers in the storage namespace before using queue actions.</summary>
    Task<bool> UpdateQueue(string id, JobQueueAction action, CancellationToken cancellationToken = default);
}
