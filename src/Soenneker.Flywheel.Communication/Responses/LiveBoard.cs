namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Current search page and optional summary for a versioned live subscription.</summary>
public sealed record LiveBoard(int Version, List<JobView> Items, int TotalCount, List<JobHistoryPoint>? History, ScheduleView? Schedules,
    long? RunningCount = null, int? ServerCount = null, int? TotalWorkers = null, List<JobHistoryPoint>? LiveActivity = null);
