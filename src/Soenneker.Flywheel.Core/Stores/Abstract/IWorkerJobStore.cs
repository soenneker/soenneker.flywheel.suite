using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Worker lifecycle operations that exclude unsupported handlers and distinguish interruption from failure.</summary>
public interface IWorkerJobStore
{
    /// <summary>Claims only a job whose handler is registered on this worker and whose version and target instance
    /// are compatible. Ineligible jobs must not consume attempts, rate limits, or concurrency permits.</summary>
    Task<JobLease?> ClaimForWorker(string owner, TimeSpan duration, string applicationVersion,
        IReadOnlySet<string> jobNames, CancellationToken cancellationToken = default);

    /// <summary>Requeues a cooperatively interrupted execution without consuming its attempt budget.
    /// Durable cancellation takes precedence. Returns false if ownership has expired or changed.</summary>
    Task<bool> Interrupt(JobLease lease, string error, CancellationToken cancellationToken = default);

    /// <summary>Dead-letters a handler that failed to stop after cancellation, regardless of retry policy,
    /// invalidating its lease and preventing automatic retries. Returns false if ownership has changed.</summary>
    Task<bool> Quarantine(JobLease lease, string error, CancellationToken cancellationToken = default);
}
