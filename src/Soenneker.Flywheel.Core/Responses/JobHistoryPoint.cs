namespace Soenneker.Flywheel.Core.Responses;

/// <summary>Job activity in a five-minute UTC bucket. Timestamp is Unix milliseconds.</summary>
/// <param name="Timestamp">Start of the five-minute bucket in UTC Unix milliseconds.</param>
/// <param name="Scheduled">Transitions into Scheduled, including retries.</param>
/// <param name="Running">Attempt starts during the bucket, not the current number of running jobs.</param>
/// <param name="Succeeded">Transitions into Succeeded.</param>
/// <param name="DeadLettered">Transitions into DeadLettered.</param>
public sealed record JobHistoryPoint(long Timestamp, int Scheduled, int Running, int Succeeded, int DeadLettered);
