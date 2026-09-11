using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <param name="Items">Items returned for the requested page.</param>
/// <param name="TotalCount">Total number of matching items before pagination.</param>
public sealed record SearchResult(
    [property: JsonPropertyName("items")] List<JobView> Items,
    [property: JsonPropertyName("totalCount")] int TotalCount);
