using Soenneker.Flywheel.Communication.Dtos;
namespace Soenneker.Flywheel.Communication.Requests;

/// <summary>Replaces a recurring definition. Exactly one of IntervalMilliseconds or Cron must be supplied.
/// ExpectedRevision optionally rejects a stale editor. Paused definitions remain paused unless Enabled is changed.</summary>
public sealed record ScheduleUpdate(string Name, string Payload, JobPolicy Policy, long IntervalMilliseconds = 0,
    string? Cron = null, string TimeZoneId = "UTC", bool IncludeSeconds = false, bool Enabled = true,
    long? ExpectedRevision = null, string? Description = null);
