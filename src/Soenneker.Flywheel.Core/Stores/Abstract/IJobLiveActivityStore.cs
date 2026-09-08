using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Reads recent state transitions and available server-recorded concurrency at live-chart resolution.</summary>
public interface IJobLiveActivityStore
{
    /// <summary>Returns the last minute, including the current second, in one-second UTC buckets.</summary>
    Task<IReadOnlyList<JobHistoryPoint>> GetLiveActivity(CancellationToken cancellationToken = default);
}
