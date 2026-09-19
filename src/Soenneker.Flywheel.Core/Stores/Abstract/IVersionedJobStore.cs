using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Durable instance-restricted submissions and version-aware lease selection.</summary>
public interface IVersionedJobStore
{
    /// <summary>Atomically submits one job per name, application version, and instance within the storage namespace.
    /// Repeated calls return its original ID, even after job retention removes the record.
    /// Retries and lease recovery may execute the handler again; handlers must be idempotent.</summary>
    Task<string> EnqueueForCurrentInstance(EnqueueRequest request, string applicationVersion, string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Claims an unrestricted job or a job whose application version exactly matches the runner.
    /// Instance-restricted work also requires the owner to match its target instance.
    /// Selection must exclude incompatible jobs before consuming attempts or distributed limits.</summary>
    Task<JobLease?> ClaimForVersion(string owner, TimeSpan duration, string applicationVersion,
        CancellationToken cancellationToken = default);
}
