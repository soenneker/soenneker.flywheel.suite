namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A recurring schedule with its interval and next due time in milliseconds.</summary>
public sealed record RecurringScheduleView(string Id, string Name, long Interval, long DueAt,
    string? Cron = null, string TimeZoneId = "UTC", bool IncludeSeconds = false);
