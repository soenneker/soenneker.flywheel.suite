using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Persists progress only while the supplied lease still owns the running job.</summary>
public interface IJobProgressStore
{
    /// <summary>Updates progress using the lease as a fencing capability. Returns false when ownership has been lost.</summary>
    Task<bool> SetProgress(JobLease lease, double percentage, string? message,
        CancellationToken cancellationToken = default);
}
