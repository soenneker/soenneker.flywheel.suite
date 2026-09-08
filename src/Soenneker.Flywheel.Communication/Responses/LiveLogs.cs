namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Bounded retained log tail for a subscribed job.</summary>
public sealed record LiveLogs(int Version, string JobId, List<LogEntry>? Entries);
