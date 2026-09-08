namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Durable job activity in a five-minute UTC bucket.</summary>
public sealed record JobHistoryPoint(long Timestamp, int Scheduled, int Running, int Succeeded, int DeadLettered);
