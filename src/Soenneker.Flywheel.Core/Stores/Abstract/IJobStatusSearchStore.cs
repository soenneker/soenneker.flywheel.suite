using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Searches jobs with status exclusions applied before counting and pagination.</summary>
public interface IJobStatusSearchStore
{
    /// <summary>Returns matching jobs in running-first, newest-first order, optionally restricted to an update time range.</summary>
    Task<JobSearchResult> Search(string? query, DateTimeOffset? startAt, DateTimeOffset? endAt,
        IReadOnlySet<string> excludedStates, int offset = 0, int count = 50, CancellationToken cancellationToken = default);
}
