using Soenneker.Cron.Parser;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    public Task<ScheduleDefinitionView?> GetRecurringDefinition(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return Mutate<ScheduleDefinitionView?>(cancellationToken, async _ =>
        {
            var s = await _schedules.Get(id);
            return s is null ? null : new(id, new(s.Job.Name, s.Job.Payload, s.Job.Policy, s.Interval,
                s.Cron, s.TimeZoneId, s.IncludeSeconds, s.Enabled, s.Revision, s.Job.Description));
        });
    }

    public Task<bool> UpdateRecurring(string id, ScheduleUpdate update, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        ArgumentNullException.ThrowIfNull(update);
        if (update.Cron is null ? update.IntervalMilliseconds < 1000 : update.IntervalMilliseconds != 0)
            throw new ArgumentException("Specify an interval of at least one second or a cron expression.");
        if (update.IntervalMilliseconds > TimeSpan.MaxValue.TotalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(update));
        CronSchedule? cron = update.Cron is null ? null : CronParser.Parse(update.Cron, update.TimeZoneId, update.IncludeSeconds);
        var job = Create(new(update.Name, update.Payload, update.Policy, TimeSpan.Zero, Description: update.Description));
        return Mutate(cancellationToken, async now =>
        {
            var existing = await _schedules.Get(id);
            if (existing is null || existing.Job.IsSystem || update.ExpectedRevision is { } revision && revision != existing.Revision) return false;
            long? next = !update.Enabled ? null : cron is null ? checked(now + update.IntervalMilliseconds) :
                cron.Value.Next(DateTimeOffset.FromUnixTimeMilliseconds(now))?.ToUnixTimeMilliseconds()
                ?? throw new ArgumentException("Cron expression has no future occurrence.");
            await _schedules.Set(id, existing with { Job = job, Interval = update.IntervalMilliseconds, Cron = update.Cron,
                TimeZoneId = update.TimeZoneId, IncludeSeconds = update.IncludeSeconds, DueAt = next,
                Enabled = update.Enabled, Revision = existing.Revision + 1 });
            return true;
        });
    }

    public Task<bool> SetRecurringEnabled(string id, bool enabled, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return Mutate(cancellationToken, async now =>
        {
            var s = await _schedules.Get(id);
            if (s is null || s.Job.IsSystem || expectedRevision is { } revision && revision != s.Revision) return false;
            if (s.Enabled == enabled) return true;
            long? next = !enabled ? null : s.Cron is null ? checked(now + s.Interval) :
                CronParser.Parse(s.Cron, s.TimeZoneId, s.IncludeSeconds).Next(DateTimeOffset.FromUnixTimeMilliseconds(now))?.ToUnixTimeMilliseconds()
                ?? throw new ArgumentException("Cron expression has no future occurrence.");
            await _schedules.Set(id, s with { Enabled = enabled, DueAt = next, Revision = s.Revision + 1 });
            return true;
        });
    }

    public Task<bool> DeleteRecurring(string id, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return Mutate(cancellationToken, async _ =>
        {
            var s = await _schedules.Get(id);
            if (s is null || s.Job.IsSystem || expectedRevision is { } revision && revision != s.Revision) return false;
            await _schedules.Remove(id);
            return true;
        });
    }

    public Task<EnqueueRequest?> GetManualDefinition(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return Mutate<EnqueueRequest?>(cancellationToken, async _ =>
        {
            var definition = await _manualJobs.Get(id);
            return definition is null ? null : new(definition.Job.Name, definition.Job.Payload, definition.Job.Policy,
                TimeSpan.Zero, Description: definition.Job.Description);
        });
    }

    public Task<bool> UpdateManual(string id, EnqueueRequest request, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        if (request.Delay != TimeSpan.Zero || request.IdempotencyKey is not null) throw new ArgumentException("Invalid manual definition.");
        var job = Create(request);
        return Mutate(cancellationToken, async _ =>
        {
            var existing = await _manualJobs.Get(id);
            if (existing is null || existing.Job.IsSystem) return false;
            await _manualJobs.Set(id, existing with { Job = job });
            return true;
        });
    }

    public Task<bool> DeleteManual(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return Mutate(cancellationToken, async _ =>
        {
            var existing = await _manualJobs.Get(id);
            if (existing is null || existing.Job.IsSystem) return false;
            await _manualJobs.Remove(id);
            return true;
        });
    }
}
