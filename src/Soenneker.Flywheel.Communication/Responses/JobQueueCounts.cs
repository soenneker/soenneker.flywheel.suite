namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Exact pending execution totals at one storage observation time.</summary>
public sealed record JobQueueCounts(long Queued, long Scheduled);
