using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

public sealed record HistoryOptions(
    [property: JsonPropertyName("retentionSeconds")] long RetentionSeconds);
