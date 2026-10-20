using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Persists best-effort lease diagnostics independently of lease-authorized handler logs.</summary>
public interface IJobLeaseDiagnosticsStore
{
    /// <summary>Records a bounded reason for the same running lease generation, even after its deadline.
    /// Returns false if maintenance or another owner has already changed that generation.
    /// Does not extend the lease or change the execution outcome.</summary>
    Task<bool> RecordLeaseLoss(JobLease lease, string reason, CancellationToken cancellationToken = default);
}
