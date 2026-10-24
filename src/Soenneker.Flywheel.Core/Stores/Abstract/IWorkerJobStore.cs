using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Worker lifecycle operations that exclude unsupported handlers and distinguish interruption from failure.</summary>
public interface IWorkerJobStore
{
    /// <summary>Claims only a job whose handler is registered on this worker and whose version and target instance
    /// are compatible. Ineligible jobs must not consume attempts, rate limits, or concurrency permits.</summary>
    Task<JobLease?> ClaimForWorker(string owner, TimeSpan duration, string applicationVersion,
        IReadOnlySet<string> jobNames, CancellationToken cancellationToken = default);

    /// <summary>Claims an eligible job assigned to the specified case-sensitive pool. Empty method pool lists
    /// belong to default. Pool mismatches must not consume attempts, rate limits, or concurrency permits.</summary>
    Task<JobLease?> ClaimForWorker(string owner, TimeSpan duration, string applicationVersion,
        IReadOnlySet<string> jobNames, string pool, CancellationToken cancellationToken = default) =>
        pool == "default" ? ClaimForWorker(owner, duration, applicationVersion, jobNames, cancellationToken)
            : throw new NotSupportedException("This job store does not support worker pools.");

    /// <summary>Requeues a cooperatively interrupted execution without consuming its attempt budget.
    /// Durable cancellation takes precedence. Returns false if ownership has expired or changed.</summary>
    Task<bool> Interrupt(JobLease lease, string error, CancellationToken cancellationToken = default);

    /// <summary>Dead-letters a handler that exceeded its cancellation grace period, regardless of retry policy.
    /// Call only after the handler has exited: invalidating its lease permits manual reruns and releases concurrency.
    /// Returns false if ownership has changed.</summary>
    Task<bool> Quarantine(JobLease lease, string error, CancellationToken cancellationToken = default);
}
