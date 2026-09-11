using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Durable once-per-application-version submissions and version-aware lease selection.</summary>
public interface IVersionedJobStore
{
    /// <summary>Atomically submits one job per name and application version within the storage namespace.
    /// Repeated calls return its original ID, even after job retention removes the record.
    /// Retries and lease recovery may execute the handler again; handlers must be idempotent.</summary>
    Task<string> RunOnceForCurrentVersion(EnqueueRequest request, string applicationVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Claims an unrestricted job or a job whose application version exactly matches the runner.
    /// Selection must exclude incompatible jobs before consuming attempts or distributed limits.</summary>
    Task<JobLease?> ClaimForVersion(string owner, TimeSpan duration, string applicationVersion,
        CancellationToken cancellationToken = default);
}
