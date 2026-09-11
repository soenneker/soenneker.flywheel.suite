using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Logging.Dtos;

/// <summary>A formatted log message emitted during a job execution.</summary>
/// <param name="Level">Severity name of the captured log message.</param>
/// <param name="Category">Logger category that emitted the message.</param>
/// <param name="Message">Message text.</param>
public sealed record JobLogMessage(
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("message")] string Message);
