namespace Soenneker.Flywheel.Communication.Responses;

/// <param name="Items">Items returned for the requested page.</param>
/// <param name="TotalCount">Total number of matching items before pagination.</param>
public sealed record SearchResult(List<JobView> Items, int TotalCount);
