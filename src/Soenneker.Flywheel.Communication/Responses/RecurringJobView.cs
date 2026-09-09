namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A recurring schedule without its payload or execution credentials. Times and intervals are in milliseconds.</summary>
public sealed record RecurringJobView(string Id, string Name, long Interval, long DueAt,
    string? Cron = null, string TimeZoneId = "UTC", bool IncludeSeconds = false, string? LastExecutionStatus = null);
