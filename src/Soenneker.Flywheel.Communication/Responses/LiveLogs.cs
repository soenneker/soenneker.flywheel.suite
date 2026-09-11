using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Bounded retained log tail for a subscribed job.</summary>
public sealed record LiveLogs(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("entries")] List<LogEntry>? Entries);
