using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Bounded, diagnostic job logs. Logs are not authoritative lifecycle state.</summary>
public interface IJobLogStore
{
    /// <summary>Appends a batch only while the lease is current and unexpired. False means ownership was lost.
    /// Providers must bound retained logs and must not accept stale owners.</summary>
    Task<bool> AppendLogs(JobLease lease, IReadOnlyList<JobLogMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the latest retained entries in chronological order. Count must be between 1 and 200.</summary>
    Task<IReadOnlyList<JobLogEntry>> GetLogs(string jobId, int count = 200,
        CancellationToken cancellationToken = default);
}