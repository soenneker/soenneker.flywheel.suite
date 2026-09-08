using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Reads a static distribution of matching retained jobs, independent of pagination.</summary>
public interface IJobSearchHistoryStore
{
    /// <summary>Counts matching jobs by current state and last update in five-minute UTC buckets, using the same filters as search.</summary>
    Task<IReadOnlyList<JobHistoryPoint>> GetSearchHistory(string? query, DateTimeOffset? startAt, DateTimeOffset? endAt,
        CancellationToken cancellationToken = default);
}
