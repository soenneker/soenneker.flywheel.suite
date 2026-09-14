using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions;
using Soenneker.Asyncs.Locks;
using System.Text.Json;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore : IJobStore, IVersionedJobStore, IJobChangeFeed, ICronJobStore,
    IJobChainStore, IMethodPolicyStore, INodeStore, IJobLogStore, IJobProgressStore, IServerStore,
    IJobScheduleStore, IRecurringJobCountStore, IRecurringJobRunner, IJobRunningCountStore,
    IJobHistoryStore, IJobLiveActivityStore, IJobSearchHistoryStore, IJobTimeRangeSearchStore, IJobLiveActivitySampler, IAsyncDisposable
{
    // Attempts on one store share this gate; Librarian conditions coordinate independent stores.
    private readonly AsyncLock _gate = new();
    private readonly LibrarianTable<string, JobRecord> _jobs;
    private readonly LibrarianTable<string, string> _dedupe;
    private readonly LibrarianTable<VersionKey, string> _versions;
    private readonly LibrarianTable<string, MethodPolicy> _policies;
    private readonly LibrarianTable<string, Rate> _rates;
    private readonly LibrarianTable<string, DispatchCandidate> _dispatch;
    private readonly LibrarianTable<string, RunningEntry> _running;
    private readonly LibrarianTable<string, string> _chainMembership;
    private readonly TimeProvider _time;
    private readonly bool _retainCompletedJobs;

    protected LibrarianJobStore(ILibrarianDatabase database, TimeSpan? historyRetention = null,
        bool retainCompletedJobs = true, TimeProvider? timeProvider = null, bool ownsDatabase = false,
        Func<CancellationToken, ValueTask<long>>? clock = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        HistoryRetention = historyRetention ?? TimeSpan.FromDays(1);
        if (HistoryRetention < TimeSpan.FromMinutes(5) || HistoryRetention > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(historyRetention));
        _retainCompletedJobs = retainCompletedJobs;
        _time = timeProvider ?? TimeProvider.System;
        _clock = clock ?? (_ => ValueTask.FromResult(_time.GetUtcNow().ToUnixTimeMilliseconds()));
        _ownsDatabase = ownsDatabase;
        _jobs = new LibrarianTable<string, JobRecord>("jobs");
        _dedupe = new LibrarianTable<string, string>("dedupe");
        _versions = new LibrarianTable<VersionKey, string>("versions");
        _policies = new LibrarianTable<string, MethodPolicy>("policies");
        _rates = new LibrarianTable<string, Rate>("rates");
        _history = new LibrarianTable<long, JobHistoryPoint>("history");
        _live = new LibrarianTable<long, JobHistoryPoint>("live");
        _samples = new LibrarianTable<long, Sample>("samples");
        _logs = new LibrarianTable<string, LogBuffer>("logs");
        _nodes = new LibrarianTable<string, Node>("nodes");
        _schedules = new LibrarianTable<string, Schedule>("schedules");
        _chains = new LibrarianTable<string, string[]>("chains");
        _metadata = new LibrarianTable<string, long>("metadata");
        _idle = new LibrarianTable<string, IdleSample>("idle");
        _dispatch = new LibrarianTable<string, DispatchCandidate>("dispatch");
        _running = new LibrarianTable<string, RunningEntry>("running");
        _chainMembership = new LibrarianTable<string, string>("chainMembership");
        _tables = [_jobs, _dedupe, _versions, _policies, _rates, _history, _live, _samples, _logs, _nodes, _schedules, _chains, _metadata, _idle, _dispatch, _running, _chainMembership];
    }

    public TimeSpan HistoryRetention { get; }

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 200) throw new ArgumentOutOfRangeException(nameof(id));
    }

    private static long Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMilliseconds(1) || duration > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(duration));
        return (long)duration.TotalMilliseconds;
    }

    private static bool Terminal(JobState state) =>
        state == JobState.Succeeded || state == JobState.Cancelled || state == JobState.DeadLettered;

    private static JobRecord Create(EnqueueRequest request)
    {
        request.Policy.Validate();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 || request.Payload.Length > 262144 ||
            request.Description?.Length > 500 || request.Delay < TimeSpan.Zero || request.Delay > TimeSpan.FromDays(365) ||
            request.IdempotencyKey?.Length > 256)
            throw new ArgumentException("Invalid job name, payload, delay or idempotency key.");
        using JsonDocument parsed = JsonDocument.Parse(request.Payload);
        return new JobRecord { Id = Guid.NewGuid().ToString("N"), Name = request.Name, Payload = request.Payload,
            Description = request.Description, Policy = request.Policy };
    }

    private async Task Save(JobRecord job, long now)
    {
        JobRecord? previous = await _jobs.Get(job.Id);
        bool transition = previous is null || previous.State != job.State;
        job = job with { UpdatedAt = now, CompletedAt = Terminal(job.State) ? transition ? now : job.CompletedAt : 0 };
        await _jobs.Set(job.Id, job).NoSync();
        if (job.State == JobState.Scheduled)
            await _dispatch.Set(job.Id, new DispatchCandidate(job.Id, job.Name, job.ApplicationVersion, job.Policy.Priority.Value, job.DueAt)).NoSync();
        else await _dispatch.Remove(job.Id).NoSync();
        if (job.State == JobState.Running)
            await _running.Set(job.Id, new RunningEntry(job.Name, job.LeaseUntil)).NoSync();
        else await _running.Remove(job.Id).NoSync();
        if (transition) await RecordTransition(job.State, now);
    }

    private async Task<string> Insert(JobRecord job, long delay, long now)
    {
        await Save(job with { CreatedAt = now, DueAt = job.State == JobState.Waiting ? 0 : now + delay }, now);
        return job.Id;
    }

    public Task<string> Enqueue(EnqueueRequest request, CancellationToken cancellationToken = default)
    {
        JobRecord job = Create(request);
        return Mutate(cancellationToken, async now =>
        {
            if (request.IdempotencyKey is { } key)
            {
                if ((await _dedupe.GetEntry(key)) is { Value: var existing }) return existing;
                await _dedupe.Set(key, job.Id).NoSync();
            }
            return await Insert(job, (long)request.Delay.TotalMilliseconds, now);
        });
    }

    public Task<string> EnqueueForCurrentVersion(EnqueueRequest request, string applicationVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateId(applicationVersion);
        if (request.IdempotencyKey is not null) throw new ArgumentException("Version submissions cannot have an idempotency key.");
        JobRecord job = Create(request) with { ApplicationVersion = applicationVersion };
        return Mutate(cancellationToken, async now =>
        {
            var key = new VersionKey(request.Name, applicationVersion);
            if ((await _versions.GetEntry(key)) is { Value: var existing }) return existing;
            await _versions.Set(key, job.Id).NoSync();
            return await Insert(job, (long)request.Delay.TotalMilliseconds, now);
        });
    }

    public Task ConfigureMethod(string name, MethodPolicy policy, CancellationToken cancellationToken = default)
    {
        ValidateId(name);
        policy.Validate();
        return Mutate(cancellationToken, async _ =>
        {
            if ((await _policies.GetEntry(name)) is not { Value: var previous } || previous != policy)
            {
                await _policies.Set(name, policy).NoSync();
                await _rates.Remove(name).NoSync();
            }
            return true;
        });
    }

    public Task<JobLease?> Claim(string owner, TimeSpan duration, CancellationToken cancellationToken = default) =>
        ClaimCore(owner, duration, null, cancellationToken);

    public Task<JobLease?> ClaimForVersion(string owner, TimeSpan duration, string applicationVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateId(applicationVersion);
        return ClaimCore(owner, duration, applicationVersion, cancellationToken);
    }

    private Task<JobLease?> ClaimCore(string owner, TimeSpan duration, string? applicationVersion, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        long milliseconds = Duration(duration);
        return Mutate<JobLease?>(ct, async now =>
        {
            if (await _dispatch.CountAll() != await _jobs.Count("state", JobState.Scheduled.Value))
                throw new InvalidDataException("Required dispatch metadata is missing or inconsistent.");
            var blocked = new HashSet<string>(StringComparer.Ordinal);
            var candidates = await _dispatch.Range("value.dueAt", maximum: now);
            foreach (DispatchCandidate candidate in candidates.Select(e => e.Value)
                .Where(c => c.ApplicationVersion is null || c.ApplicationVersion == applicationVersion)
                .OrderByDescending(c => c.Priority).ThenBy(c => c.DueAt).ThenBy(c => c.Id, StringComparer.Ordinal))
            {
                if (blocked.Contains(candidate.Name)) continue;
                MethodPolicy? configured = await _policies.Get(candidate.Name);
                if (configured is not null)
                {
                    var usage = await _rates.Get(candidate.Name);
                    if ((configured.MaxConcurrency is { } max && (await _running.Find("name", candidate.Name)).Count(r => r.LeaseUntil > now) >= max) ||
                        (configured.RateLimit is { } limit && usage.Until > now && usage.Count >= limit))
                    {
                        blocked.Add(candidate.Name);
                        continue;
                    }
                }
                JobRecord job = await _jobs.Get(candidate.Id) ?? throw new InvalidDataException("Dispatch document has no job.");
                if (job.State != JobState.Scheduled || job.DueAt != candidate.DueAt || job.Name != candidate.Name ||
                    job.ApplicationVersion != candidate.ApplicationVersion || job.Policy.Priority.Value != candidate.Priority)
                    throw new InvalidDataException("Dispatch document does not match its job.");
                if ((await _policies.GetEntry(job.Name)) is { Value: var policy } && policy.RateLimit is not null)
                {
                    var rate = (await _rates.Get(job.Name));
                    if (rate.Until <= now) rate = new Rate(now + (long)policy.RateWindow.TotalMilliseconds, 0);
                    await _rates.Set(job.Name, new Rate(rate.Until, rate.Count + 1)).NoSync();
                }
                JobRecord claimed = job with { State = JobState.Running, Attempt = job.Attempt + 1,
                    Version = job.Version + 1, Token = Guid.NewGuid().ToString("N"), Owner = owner,
                    LeaseUntil = now + milliseconds, StartedAt = now, UpdatedAt = now, CompletedAt = 0,
                    Progress = null, ProgressMessage = null, ProgressUpdatedAt = 0 };
                await Save(claimed, now);
                return new JobLease(claimed, claimed.Token!, claimed.Version);
            }
            return null;
        });
    }

    private async ValueTask<JobRecord?> Owned(JobLease lease, long now) =>
        (await _jobs.GetEntry(lease.Job.Id)) is { Value: var job } && job.State == JobState.Running &&
        job.Token == lease.Token && job.Version == lease.Version && job.LeaseUntil > now ? job : null;

    public Task<LeaseStatus> Renew(JobLease lease, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        long milliseconds = Duration(duration);
        return Mutate(cancellationToken, async now =>
        {
            JobRecord? job = await Owned(lease, now);
            if (job is null) return LeaseStatus.Lost;
            await Save(job with { LeaseUntil = now + milliseconds }, now);
            return job.CancelRequested ? LeaseStatus.CancellationRequested : LeaseStatus.Renewed;
        });
    }

    public Task<bool> Finish(JobLease lease, JobOutcome outcome, string? error, TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        if (!JobOutcome.IsDefined(outcome.Value) || retryDelay < TimeSpan.Zero || retryDelay > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException();
        return Mutate(cancellationToken, async now =>
        {
            JobRecord? job = await Owned(lease, now);
            if (job is null) return false;
            JobState state = job.CancelRequested || outcome == JobOutcome.Cancelled ? JobState.Cancelled :
                outcome == JobOutcome.Succeeded ? JobState.Succeeded :
                job.Attempt >= job.Policy.MaxAttempts ? JobState.DeadLettered : JobState.Scheduled;
            JobRecord finished = job with { State = state, Token = null, Owner = null, LeaseUntil = 0,
                Version = job.Version + 1, Error = error is null ? "" : error[..Math.Min(error.Length, 1024)],
                Progress = outcome == JobOutcome.Succeeded && job.Progress is not null ? 100 : job.Progress,
                DueAt = state == JobState.Scheduled ? now + (long)retryDelay.TotalMilliseconds : job.DueAt };
            await Save(finished, now);
            await AdvanceChain(finished, now);
            return true;
        });
    }

    public Task<bool> Cancel(string id, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        return Mutate(cancellationToken, async now =>
        {
            if ((await _jobs.GetEntry(id)) is not { Value: var job } || Terminal(job.State)) return false;
            JobRecord cancelled = job with { CancelRequested = true };
            if (job.State != JobState.Running)
                cancelled = cancelled with { State = JobState.Cancelled, Version = job.Version + 1 };
            await Save(cancelled, now);
            await AdvanceChain(cancelled, now);
            return true;
        });
    }
}
