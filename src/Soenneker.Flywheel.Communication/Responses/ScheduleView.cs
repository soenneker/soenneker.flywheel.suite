namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Registered recurring schedules and pending executions, ordered by next due time.</summary>
public sealed record ScheduleView(List<RecurringScheduleView> Recurring, List<JobView> Scheduled);
