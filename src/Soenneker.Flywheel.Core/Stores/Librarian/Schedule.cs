using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record Schedule(JobRecord Job, long Interval, long? DueAt, string? Cron = null,
    string TimeZoneId = "UTC", bool IncludeSeconds = false, string? LastExecutionId = null,
    string? LastExecutionStatus = null);
