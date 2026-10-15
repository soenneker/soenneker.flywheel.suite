using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Provides queue totals independently of paginated job lists.</summary>
public interface IJobQueueCountStore
{
    /// <summary>Counts all pending executions: queued jobs are due now, scheduled jobs are due later.
    /// Both counts use the same storage snapshot and clock, including retries and recurring occurrences.</summary>
    Task<JobQueueCounts> GetQueueCounts(CancellationToken cancellationToken = default);
}
