using Soenneker.Extensions.ValueTask;
using Soenneker.Cron.Parser;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    private readonly LibrarianTable<string, Schedule> _schedules;
    private readonly LibrarianTable<string, string[]> _chains;

    public Task<IReadOnlyList<string>> EnqueueChain(IReadOnlyList<EnqueueRequest> steps, string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (steps.Count is < 1 or > 100 || idempotencyKey?.Length > 256 || steps.Any(s => s.IdempotencyKey is not null))
            throw new ArgumentException("Chains require 1–100 steps without step idempotency keys.");
        EnqueueRequest[] requests = steps.ToArray();
        JobRecord[] jobs = requests.Select(Create).ToArray();
        return Mutate<IReadOnlyList<string>>(cancellationToken, async now =>
        {
            if (idempotencyKey is not null && (await _chains.GetEntry(idempotencyKey)) is { Value: var existing })
                return existing.ToArray();
            string[] ids = jobs.Select(j => j.Id).ToArray();
            string workflowId = Guid.NewGuid().ToString("N");
            await _workflows.Set(workflowId, new(workflowId, ids, []));
            for (int i = 0; i < jobs.Length; i++)
            {
                if (i + 1 < ids.Length) await _dependents.Set(ids[i], [ids[i + 1]]);
                await Insert(jobs[i] with { State = i == 0 ? JobState.Scheduled : JobState.Waiting,
                    WorkflowId = workflowId, PredecessorIds = i == 0 ? [] : [ids[i - 1]],
                    ParentJobId = i == 0 ? null : ids[i - 1], NextJobId = i + 1 == ids.Length ? null : ids[i + 1],
                    DelayAfterParent = i == 0 ? 0 : (long)requests[i].Delay.TotalMilliseconds },
                    i == 0 ? (long)requests[i].Delay.TotalMilliseconds : 0, now);
            }
            if (idempotencyKey is not null)
            {
                await _chains.Set(idempotencyKey, ids).NoSync();
                foreach (string id in ids) await _chainMembership.Set(id, idempotencyKey).NoSync();
            }
            return ids.ToArray();
        });
    }

    private async Task AdvanceChain(JobRecord parent, long now)
    {
        if (!Terminal(parent.State)) return;
        await AdvanceWorkflow(parent, now);
        string? nextId = parent.NextJobId;
        while (nextId is not null && (await _jobs.GetEntry(nextId)) is { Value: var next } && next.State == JobState.Waiting)
        {
            if (parent.State == JobState.Succeeded)
            {
                await Save(next with { State = JobState.Scheduled, DueAt = now + next.DelayAfterParent }, now);
                return;
            }
            var cancelled = next with { State = JobState.Cancelled, CancelRequested = true, DependencySkipped = true, Version = next.Version + 1,
                Error = "Predecessor did not succeed." };
            await Save(cancelled, now);
            await AdvanceWorkflow(cancelled, now);
            nextId = next.NextJobId;
        }
    }

    public Task<bool> AddRecurring(string id, EnqueueRequest request, TimeSpan interval,
        CancellationToken cancellationToken = default, bool coalesceOccurrences = false)
    {
        ValidateId(id);
        if (request.IdempotencyKey is not null || interval < TimeSpan.FromSeconds(1))
            throw new ArgumentException("Invalid recurring schedule.");
        long milliseconds = Duration(interval);
        JobRecord job = Create(request);
        return Mutate(cancellationToken, async now =>
        {
            if (!await _schedules.TryAdd(id, new Schedule(job, milliseconds, now, CoalesceOccurrences: coalesceOccurrences)).NoSync()) return false;
            return true;
        });
    }

    public Task<bool> AddCron(string id, EnqueueRequest request, string expression, string timeZoneId = "UTC",
        bool includeSeconds = false, CancellationToken cancellationToken = default, bool coalesceOccurrences = false)
    {
        ValidateId(id);
        if (request.IdempotencyKey is not null || request.Delay != TimeSpan.Zero)
            throw new ArgumentException("Invalid cron schedule.");
        CronSchedule cron = CronParser.Parse(expression, timeZoneId, includeSeconds);
        JobRecord job = Create(request);
        return Mutate(cancellationToken, async now =>
        {
            if ((await _schedules.ContainsKey(id))) return false;
            long? next = cron.Next(DateTimeOffset.FromUnixTimeMilliseconds(now))?.ToUnixTimeMilliseconds()
                ?? throw new ArgumentException("Cron expression has no future occurrence.", nameof(expression));
            await _schedules.Set(id, new Schedule(job, 0, next, expression, timeZoneId, includeSeconds, CoalesceOccurrences: coalesceOccurrences)).NoSync();
            return true;
        });
    }

    private async Task<string> Occurrence(string id, Schedule schedule, long now, long? scheduledFor = null)
    {
        if (schedule.CoalesceOccurrences && await _scheduleExecutions.FindFirst("scheduleId", id) is { } outstanding)
        {
            await _schedules.Set(id, schedule with { CoalescedOccurrences = checked(schedule.CoalescedOccurrences + 1) }).NoSync();
            return outstanding.JobId;
        }
        string executionId = await Insert(schedule.Job with { Id = Guid.NewGuid().ToString(), ScheduleId = id,
            ScheduledFor = scheduledFor ?? now }, 0, now);
        await _schedules.Set(id, schedule with { LastExecutionId = executionId, LastExecutionStatus = null }).NoSync();
        return executionId;
    }

    public Task<string?> RunRecurring(string scheduleId, CancellationToken cancellationToken = default)
    {
        ValidateId(scheduleId);
        return Mutate<string?>(cancellationToken, async now =>
            (await _schedules.GetEntry(scheduleId)) is { Value: var schedule } ? await Occurrence(scheduleId, schedule, now) : null);
    }

    public Task<long> GetRecurringCount(CancellationToken cancellationToken = default) =>
        Mutate(cancellationToken, async _ => (long)await _schedules.CountAll());

    public Task<IReadOnlyList<RecurringJobView>> ListRecurring(int count = 50, CancellationToken cancellationToken = default)
    {
        ValidatePage(0, count);
        return Mutate<IReadOnlyList<RecurringJobView>>(cancellationToken, async now =>
        {
            var schedules = await _schedules.Range<RecurringScheduleSummary>("order", minimum: "", take: count);
            string[] ids = schedules.Where(pair => pair.Value.LastExecutionId is not null).Select(pair => pair.Value.LastExecutionId!).ToArray();
            RecurringJobSummary?[] executions = await _jobs.GetMany<RecurringJobSummary>(ids);
            var result = new RecurringJobView[schedules.Count];
            int executionIndex = 0;
            for (int i = 0; i < schedules.Count; i++)
            {
                var pair = schedules[i];
                RecurringScheduleSummary s = pair.Value;
                RecurringJobSummary? execution = s.LastExecutionId is null ? null : executions[executionIndex++];
                string? status = execution is null ? s.LastExecutionStatus : execution.DisplayState(now);
                result[i] = new RecurringJobView(pair.Key, s.Job.Name, s.Interval, s.DueAt, s.Cron,
                    s.TimeZoneId, s.IncludeSeconds, status, s.LastExecutionId, s.Job.IsSystem, s.Enabled, s.Revision, s.CoalesceOccurrences, s.CoalescedOccurrences);
            }
            return result;
        });
    }

    public Task Maintain(int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        return Mutate(cancellationToken, async now =>
        {
            var expiredLeases = await _running.Range("value.leaseUntil", minimum: long.MinValue, maximum: now, take: batchSize);
            var dueSchedules = await _schedules.Range("value.dueAt", minimum: long.MinValue, maximum: now, take: batchSize);
            int retentionOffset = (int)Math.Min(int.MaxValue, await _metadata.Get("retentionOffset"));
            var expiredJobs = (await _jobs.Range("completedAt", minimum: long.MinValue,
                maximum: _retainCompletedJobs ? now - (long)HistoryRetention.TotalMilliseconds : long.MaxValue, skip: retentionOffset, take: batchSize)).Select(e => e.Value).ToList();
            var expiredHistory = await _history.Range("key", maximum: now / 300000 * 300000 - (long)HistoryRetention.TotalMilliseconds - 1, take: batchSize);
            var expiredLogs = await _logs.Range("value.expiresAt", minimum: long.MinValue, maximum: now, take: batchSize);
            // Retire persisted schedules and queued executions from the removed archive exporter.
            const string retiredExporter = "Flywheel.LogArchive.Export";
            if (await _schedules.Get(retiredExporter) is { Job.IsSystem: true })
                await _schedules.Remove(retiredExporter);
            foreach (var pending in await _dispatch.Range("value.name", minimum: retiredExporter, maximum: retiredExporter, take: batchSize))
            {
                if (await _jobs.Get(pending.Key) is { IsSystem: true } job)
                    await Save(job with { State = JobState.Cancelled, CancelRequested = true, Version = job.Version + 1,
                        Error = "Log archiving has been removed." }, now);
            }
            foreach (var lease in expiredLeases)
            {
                JobRecord job = await _jobs.Get(lease.Key) ?? throw new InvalidDataException("Running metadata has no job.");
                if (job.State != JobState.Running || job.LeaseUntil != lease.Value.LeaseUntil)
                    throw new InvalidDataException("Running metadata does not match its job.");
                JobState state = job.CancelRequested ? JobState.Cancelled :
                    job.StaleRestartCount >= job.Policy.MaxStaleRestarts ? JobState.DeadLettered : JobState.Scheduled;
                long delay = (long)job.Policy.RetryDelay(job.StaleRestartCount + 1, Random.Shared.NextDouble()).TotalMilliseconds;
                JobRecord recovered = job with { StaleRestartCount = job.StaleRestartCount + (state == JobState.Scheduled ? 1 : 0), State = state, Token = null, Owner = null, LeaseUntil = 0,
                    LastOwner = job.Owner, LastLeaseUntil = job.LeaseUntil,
                    LeaseLossReason = job.LeaseLossReason ?? "The lease deadline passed without a successful renewal. The worker did not record a cause; check its host logs for shutdown, restart, or storage failures.",
                    Version = job.Version + 1, Error = "Lease expired", DueAt = state == JobState.Scheduled ? now + delay : job.DueAt };
                await Save(recovered, now);
                await AdvanceChain(recovered, now);
                if (!_retainCompletedJobs && Terminal(recovered.State) && expiredJobs.Count < batchSize)
                    expiredJobs.Add((await _jobs.Get(job.Id))!);
            }
            foreach (var pair in dueSchedules)
            {
                Schedule s = pair.Value;
                if (pair.Key == retiredExporter && s.Job.IsSystem) continue;
                long due = s.DueAt!.Value;
                long? next = s.Cron is null ? due + ((now - due) / s.Interval + 1) * s.Interval :
                    CronParser.Parse(s.Cron, s.TimeZoneId, s.IncludeSeconds)
                        .Next(DateTimeOffset.FromUnixTimeMilliseconds(now))?.ToUnixTimeMilliseconds();
                await Occurrence(pair.Key, s with { DueAt = next }, now, due);
            }
            int candidateCount = expiredJobs.Count;
            expiredJobs = await RetainWorkflowMembers(expiredJobs, now);
            // Move past protected workflow members so they cannot starve unrelated history cleanup.
            await _metadata.Set("retentionOffset", candidateCount < batchSize ? 0 :
                (long)retentionOffset + Math.Max(0, candidateCount - expiredJobs.Count));
            foreach (JobRecord job in expiredJobs)
            {
                if (job.ScheduleId is { } scheduleId && (await _schedules.GetEntry(scheduleId)) is { Value: var s } && s.LastExecutionId == job.Id)
                    await _schedules.Set(scheduleId, s with { LastExecutionStatus = job.DisplayState(now) }).NoSync();
                if (job.ManualJobId is { } manualId && await _manualJobs.Get(manualId) is { } manual && manual.LastExecutionId == job.Id)
                    await _manualJobs.Set(manualId, manual with { LastExecutionStatus = job.DisplayState(now) }).NoSync();
                await _jobs.Remove(job.Id).NoSync();
                if (await _logs.Get(job.Id) is { } jobLogs) await PruneLogPages(job.Id, jobLogs, now);
                foreach (var marker in await _dedupe.FindEntries("value", job.Id)) await _dedupe.Remove(marker.Key).NoSync();
                if (await _chainMembership.Get(job.Id) is { } chain)
                {
                    await _chains.Remove(chain).NoSync();
                    await _chainMembership.Remove(job.Id).NoSync();
                }
            }
            foreach (var bucket in expiredHistory) await _history.Remove(bucket.Key).NoSync();
            await PruneLive(now);
            await PruneServers(now);
            foreach (var rate in await _rates.Range("value.until", minimum: long.MinValue, maximum: now, take: batchSize)) await _rates.Remove(rate.Key).NoSync();
            foreach (var log in expiredLogs)
                if (await _logs.Get(log.Key) is { } remaining) await PruneLogPages(log.Key, remaining, now);
            return true;
        });
    }
}
