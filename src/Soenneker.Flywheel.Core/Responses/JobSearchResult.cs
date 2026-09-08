using Soenneker.Flywheel.Core.Dtos;

namespace Soenneker.Flywheel.Core.Responses;

/// <summary>A page of matching jobs and the total matching count in the live view.</summary>
/// <param name="Items">Items returned for the requested page.</param>
/// <param name="TotalCount">Total number of matching items before pagination.</param>
public sealed record JobSearchResult(IReadOnlyList<JobRecord> Items, int TotalCount);
