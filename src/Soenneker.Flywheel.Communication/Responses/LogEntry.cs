namespace Soenneker.Flywheel.Communication.Responses;

/// <param name="Id">Identifier of the retained log entry.</param>
/// <param name="Timestamp">Log timestamp in UTC Unix milliseconds.</param>
/// <param name="Attempt">Execution attempt number.</param>
/// <param name="Level">Severity name of the captured log message.</param>
/// <param name="Category">Logger category that emitted the message.</param>
/// <param name="Message">Message text.</param>
public sealed record LogEntry(string Id, long Timestamp, int Attempt, string Level, string Category, string Message);
