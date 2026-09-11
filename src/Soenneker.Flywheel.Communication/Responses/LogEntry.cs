using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <param name="Id">Identifier of the retained log entry.</param>
/// <param name="Timestamp">Log timestamp in UTC Unix milliseconds.</param>
/// <param name="Attempt">Execution attempt number.</param>
/// <param name="Level">Severity name of the captured log message.</param>
/// <param name="Category">Logger category that emitted the message.</param>
/// <param name="Message">Message text.</param>
public sealed record LogEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("attempt")] int Attempt,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message);
