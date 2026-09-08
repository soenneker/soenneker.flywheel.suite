using Soenneker.Flywheel.Core.Responses;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Searches retained jobs whose latest activity falls within a UTC time range.</summary>
public interface IJobTimeRangeSearchStore
{
    /// <summary>Searches jobs updated at or after <paramref name="startAt"/> and before <paramref name="endAt"/>.
    /// Matching is otherwise identical to <see cref="IJobStore.Search"/>, and results are ordered newest first.</summary>
    Task<JobSearchResult> Search(string? query, DateTimeOffset startAt, DateTimeOffset endAt, int offset = 0, int count = 50,
        CancellationToken cancellationToken = default);
}
