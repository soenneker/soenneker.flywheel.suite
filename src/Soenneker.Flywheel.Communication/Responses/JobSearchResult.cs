using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A page of matching jobs and the total matching count in the live view.</summary>
/// <param name="Items">Items returned for the requested page.</param>
/// <param name="TotalCount">Total number of matching items before pagination.</param>
public sealed record JobSearchResult(IReadOnlyList<JobRecord> Items, int TotalCount);
