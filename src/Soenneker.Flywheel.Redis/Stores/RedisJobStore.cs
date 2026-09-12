using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Soenneker.Utils.Json;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Redis.Client.Abstract;
using Soenneker.Redis.Semaphores;
using Soenneker.Redis.Util.Atomics;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore : IJobRunningCountStore, IJobStore, IVersionedJobStore, IJobTimeRangeSearchStore, INodeStore,
    IJobLogStore, IJobScheduleStore, IRecurringJobCountStore, IJobHistoryStore, IRecurringJobRunner, IMethodPolicyStore, IJobChangeFeed,
    ICronJobStore, IJobChainStore, IJobProgressStore, IServerStore, IJobLiveActivityStore, IJobSearchHistoryStore
{
    private readonly Func<CancellationToken, Task<IDatabase>> _database;
    private readonly string _prefix;

    public async Task<long> GetRunningCount(CancellationToken cancellationToken = default)
    {
        IDatabase db = await Database(cancellationToken);
        return await db.SortedSetLengthAsync(Running).WaitAsync(cancellationToken);
    }

    private readonly RedisKey Jobs;
    private readonly RedisKey Due;
    private readonly RedisKey Running;
    private readonly RedisKey All;
    private readonly RedisKey Dedupe;
    private readonly RedisKey DedupeReverse;
    private readonly RedisKey Schedules;
    private readonly RedisKey ScheduleDue;
    private readonly RedisKey Nodes;
    private readonly RedisKey NodeWorkers;
    private readonly RedisKey History;
    private readonly RedisKey HistoryBuckets;
    private readonly RedisKey Completed;
    private readonly RedisKey Policies;
    private readonly RedisKey Rates;
    private readonly RedisKey Revision;
    private readonly RedisKey Dispatch;
    private readonly RedisKey Permits;
    private readonly RedisKey ChainDedupe;
    private readonly RedisKey LiveSamples;
    private readonly RedisKey[] _liveSampleKeys;
    private readonly RedisKey[] _heartbeatKeys;
    private readonly RedisKey[] _timeKeys;
    private RedisKey LeaseKey(string id) => _prefix + "lease:" + Key(id);
    private RedisKey LogKey(string id) => _prefix + "jobs:logs:" + Key(id);

    /// <summary>Creates a store using the configured shared client, database, namespace, and retention settings.</summary>
    public RedisJobStore(IRedisClient client, FlywheelRedisOptions options) : this(
        async ct => (await client.Get(options.ConnectionString, ct)).GetDatabase(options.Database), options.Namespace,
        options.HistoryRetention, options.RetainCompletedJobs)
    {
    }

    /// <summary>Creates a store from an asynchronous database factory; callers retain ownership of the connection.</summary>
    /// <param name="database">Resolves the database for each storage operation.</param>
    /// <param name="storageNamespace">Nonblank isolation namespace, encoded into a Redis Cluster hash tag.</param>
    /// <param name="historyRetention">Aggregate retention, defaulting to one day when omitted.</param>
    /// <param name="retainCompletedJobs">Retains terminal records and logs until retention expires when true.</param>
    public RedisJobStore(Func<CancellationToken, Task<IDatabase>> database, string storageNamespace,
        TimeSpan? historyRetention = null, bool retainCompletedJobs = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageNamespace);
        _database = database;
        HistoryRetention = historyRetention ?? TimeSpan.FromDays(1);
        _retainCompletedJobs = retainCompletedJobs;
        _prefix = $"flywheel:{{{Key(storageNamespace)}}}:v1:";
        Jobs = _prefix + "jobs";
        Due = _prefix + "due";
        Running = _prefix + "running";
        All = _prefix + "all";
        Dedupe = _prefix + "dedupe";
        DedupeReverse = _prefix + "dedupe-reverse";
        Schedules = _prefix + "schedules";
        ScheduleDue = _prefix + "schedule-due";
        Nodes = _prefix + "nodes";
        NodeWorkers = _prefix + "node-workers";
        History = _prefix + "history";
        HistoryBuckets = _prefix + "history-buckets";
        Completed = _prefix + "completed";
        Policies = _prefix + "function-policies";
        Rates = _prefix + "function-rates";
        Revision = _prefix + "revision";
        Dispatch = _prefix + "dispatch";
        Permits = _prefix + "job-permits";
        ChainDedupe = _prefix + "chain-dedupe";
        LiveSamples = _prefix + "activity:samples";
        _liveSampleKeys = [Running, Due, LiveSamples, Revision];
        _heartbeatKeys = [Nodes, NodeWorkers];
        _timeKeys = [Jobs];
    }

    public TimeSpan HistoryRetention { get; }
    private readonly bool _retainCompletedJobs;

    private static bool IsTerminal(JobState state) => state == JobState.Succeeded || state == JobState.DeadLettered ||
                                                      state == JobState.Cancelled;

    private static string Key(string value)
    {
        int capacity = Encoding.UTF8.GetMaxByteCount(value.Length);
        Span<byte> utf8 = capacity <= 1024 ? stackalloc byte[capacity] : new byte[capacity];
        int length = Encoding.UTF8.GetBytes(value, utf8);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8[..length], hash);
        return Convert.ToHexString(hash);
    }

    private static byte[] Serialize<T>(T value) => JsonUtil.SerializeToUtf8Bytes(value!);

    private static T? Decode<T>(RedisValue value) where T : class =>
        value.IsNull ? null : JsonUtil.Deserialize<T>(((ReadOnlyMemory<byte>)value).Span);

    private ValueTask<IDatabase> Database(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new ValueTask<IDatabase>(_database(ct).WaitAsync(ct));
    }

    // Supplying the jobs key routes TIME to its current primary in one round trip, including after a cluster
    // failover. Both IdentifyEndpoint overloads perform network I/O; an endpoint cache would become stale.
    private const string TimeScript = """
        local clock = redis.call('TIME')
        return tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
        """;

    private async Task<long> Time(IDatabase db, CancellationToken ct) =>
        (long)await db.ScriptEvaluateAsync(TimeScript, _timeKeys, flags: CommandFlags.DemandMaster).WaitAsync(ct);

    private async Task<Mutation> Begin(IDatabase db, CancellationToken ct)
    {
        // Every lifecycle writer checks and advances this revision. It protects selection across multiple reads,
        // including priority order and runtime policy changes, without holding a distributed lock.
        Task<RedisValue> revisionTask = db.StringGetAsync(Revision);
        Task<long> timeTask = Time(db, ct);
        await Task.WhenAll(revisionTask, timeTask).WaitAsync(ct);
        RedisValue revision = revisionTask.Result;
        var tx = new RedisAtomicTransaction(db);
        tx.Require(RedisAtomics.StringMatches(Revision, revision));
        tx.Queue(t => t.StringIncrementAsync(Revision));
        return new(tx, timeTask.Result, ChangeChannel(db));
    }

    private static async Task Retry(int attempt, CancellationToken ct)
    {
        if (attempt >= 100)
            throw new TimeoutException("Redis transaction repeatedly conflicted with other writers.");
        await Task.Delay(Random.Shared.Next(1, Math.Min(25, attempt + 3)), ct);
    }

    private static long Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMilliseconds(1) || duration > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(duration));
        return (long)duration.TotalMilliseconds;
    }

    private static JobRecord Create(EnqueueRequest request, string id)
    {
        request.Policy.Validate();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 || request.Payload.Length > 262144 ||
            request.Description?.Length > 500 || request.Delay < TimeSpan.Zero ||
            request.Delay > TimeSpan.FromDays(365) || request.IdempotencyKey?.Length > 256)
            throw new ArgumentException("Invalid job name, payload, delay or idempotency key.");
        using JsonDocument parsed = JsonDocument.Parse(request.Payload);
        return new JobRecord
        {
            Id = id, Name = request.Name, Description = request.Description, Payload = request.Payload,
            Policy = request.Policy
        };
    }

    private void Save(Mutation mutation, JobRecord job, JobRecord? previous = null)
    {
        if (IsTerminal(job.State) && (previous is null || previous.State != job.State))
            job = job with { CompletedAt = mutation.Now };
        else if (!IsTerminal(job.State))
            job = job with { CompletedAt = 0 };
        RedisAtomicTransaction tx = mutation.Transaction;
        tx.Queue(t => t.HashSetAsync(Jobs, job.Id,
            Serialize(job.UpdatedAt == mutation.Now ? job : job with { UpdatedAt = mutation.Now })));
        SaveDispatchMetadata(tx, job, previous);
        Publish(mutation, new JobChange("Job", job.Id));
        if (previous is not null && previous.State == job.State)
            return;
        long bucket = mutation.Now / 300000 * 300000;
        RedisKey liveKey = _prefix + "activity:" + mutation.Now / 1000;
        tx.Queue(t => t.HashIncrementAsync(liveKey, job.State.Value));
        tx.Queue(t => t.KeyExpireAsync(liveKey, LiveActivityExpiry(mutation.Now)));
        tx.Queue(t => t.HashIncrementAsync(History, $"{bucket}:{job.State.Value}"));
        tx.Queue(t => t.SortedSetAddAsync(HistoryBuckets, bucket, bucket));
        if (IsTerminal(job.State))
            tx.Queue(t => t.SortedSetAddAsync(Completed, job.Id, mutation.Now));
        else
            tx.Queue(t => t.SortedSetRemoveAsync(Completed, job.Id));
    }

    private void Insert(Mutation mutation, JobRecord job, long delay)
    {
        job = job with
        {
            CreatedAt = mutation.Now, UpdatedAt = mutation.Now,
            DueAt = job.State == JobState.Waiting ? 0 : mutation.Now + delay
        };
        Save(mutation, job);
        if (job.State == JobState.Scheduled)
            mutation.Transaction.Queue(t => t.SortedSetAddAsync(Due, job.Id, job.DueAt));
        mutation.Transaction.Queue(t => t.SortedSetAddAsync(All, job.Id, mutation.Now));
    }

    public async Task<string> Enqueue(EnqueueRequest request, CancellationToken cancellationToken = default)
    {
        JobRecord job = Create(request, Guid.NewGuid().ToString("N"));
        IDatabase db = await Database(cancellationToken);
        string? key = request.IdempotencyKey is null ? null : Key(request.IdempotencyKey);
        for (var attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, cancellationToken);
            if (key is not null)
            {
                RedisValue existing = await db.HashGetAsync(Dedupe, key).WaitAsync(cancellationToken);
                if (!existing.IsNull)
                    return (string)existing!;
                mutation.Transaction.Queue(t => t.HashSetAsync(Dedupe, key, job.Id));
                mutation.Transaction.Queue(t => t.HashSetAsync(DedupeReverse, job.Id, $"j:{key}"));
            }

            Insert(mutation, job, (long)request.Delay.TotalMilliseconds);
            if (await mutation.Transaction.Execute(cancellationToken))
                return job.Id;
            await Retry(attempt, cancellationToken);
        }
    }

    private static readonly StoredPolicy DefaultPolicy = new();

    public async Task ConfigureMethod(string name, MethodPolicy policy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new ArgumentException("Invalid job name.", nameof(name));
        policy.Validate();
        var stored = new StoredPolicy(policy.MaxConcurrency ?? 0, policy.RateLimit ?? 0,
            (long)policy.RateWindow.TotalMilliseconds);
        IDatabase db = await Database(cancellationToken);
        for (var attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, cancellationToken);
            if (Decode<StoredPolicy>(await db.HashGetAsync(Policies, name).WaitAsync(cancellationToken)) == stored)
                return;
            mutation.Transaction.Queue(t => t.HashSetAsync(Policies, name, Serialize(stored)));
            mutation.Transaction.Queue(t => t.HashDeleteAsync(Rates, name));
            if (await mutation.Transaction.Execute(cancellationToken))
                return;
            await Retry(attempt, cancellationToken);
        }
    }

    public Task<JobLease?> Claim(string owner, TimeSpan duration, CancellationToken cancellationToken = default) =>
        ClaimCore(owner, duration, null, cancellationToken);

    private async Task<JobLease?> ClaimCore(string owner, TimeSpan duration, string? applicationVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException(nameof(owner));
        long milliseconds = Duration(duration);
        IDatabase db = await Database(cancellationToken);
        // An empty queue needs neither server time nor a revision snapshot. A concurrent enqueue is picked up
        // by the change feed (or the bounded recovery poll), just as it is after an empty candidate read.
        if (await db.SortedSetLengthAsync(Due).WaitAsync(cancellationToken) == 0)
            return null;
        for (var attempt = 0;; attempt++)
        {
            // Read-only snapshot first: empty polls must not allocate a transaction or inspect running jobs.
            Task<RedisValue> revisionTask = db.StringGetAsync(Revision);
            Task<long> timeTask = Time(db, cancellationToken);
            await Task.WhenAll(revisionTask, timeTask).WaitAsync(cancellationToken);
            RedisValue revision = revisionTask.Result;
            long now = timeTask.Result;
            DispatchCandidate[] candidates = await ReadCandidates(db, now, applicationVersion, revision, cancellationToken);
            if (candidates.Length == 0)
                return null;
            StoredPolicy[] policies = await ReadPolicies(db, candidates, cancellationToken);
            Rate?[] rates = await ReadRates(db, candidates, policies, cancellationToken);
            var tx = new RedisAtomicTransaction(db);
            tx.Require(RedisAtomics.StringMatches(Revision, revision));
            tx.Queue(t => t.StringIncrementAsync(Revision));
            var mutation = new Mutation(tx, now, ChangeChannel(db));
            Dictionary<string, int>? active = null;
            var conflict = false;
            for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                DispatchCandidate candidate = candidates[candidateIndex];
                StoredPolicy policy = policies[candidateIndex];
                // Load names, not payloads, and only if an eligible function actually has a concurrency limit.
                if (policy.MaxConcurrency > 0)
                {
                    active ??= await ReadActiveCounts(db, mutation.Now, revision, cancellationToken);
                    if (active.GetValueOrDefault(candidate.Name) >= policy.MaxConcurrency)
                        continue;
                }

                Rate? rate = null;
                if (policy.RateLimit > 0)
                {
                    rate = rates[candidateIndex];
                    if (rate is null || rate.Until <= mutation.Now)
                        rate = new(mutation.Now + policy.RateWindow, 0);
                    if (rate.Count >= policy.RateLimit)
                        continue;
                }

                RedisSemaphorePermit? permit = null;
                if (policy.MaxConcurrency > 0)
                {
                    permit = await RedisSemaphore.TryAcquirePermit(db, _prefix + "function:" + Key(candidate.Name),
                        policy.MaxConcurrency, duration, cancellationToken);
                    if (permit is null)
                        continue;
                }

                var dispatched = false;
                try
                {
                    mutation = mutation with { Now = await Time(db, cancellationToken) };
                    if (rate is not null && rate.Until <= mutation.Now)
                        rate = new(mutation.Now + policy.RateWindow, 0);
                    JobRecord? job = Decode<JobRecord>(await db.HashGetAsync(Jobs, candidate.Id).WaitAsync(cancellationToken));
                    if (job is null)
                    {
                        conflict = true;
                        break;
                    }
                    RedisKey leaseKey = LeaseKey(job.Id);
                    if (permit is not null)
                    {
                        tx.Require(permit.OwnershipCondition);
                        tx.Queue(t => t.HashSetAsync(Permits, job.Id, Serialize(new StoredPermit(permit.Key, permit.Token))));
                    }

                    var token = Guid.NewGuid().ToString("N");
                    JobRecord claimed = job with
                    {
                        State = JobState.Running, Attempt = job.Attempt + 1, Version = job.Version + 1,
                        Token = token, Owner = owner, LeaseUntil = mutation.Now + milliseconds,
                        UpdatedAt = mutation.Now, StartedAt = mutation.Now, CompletedAt = 0,
                        Progress = null, ProgressMessage = null, ProgressUpdatedAt = 0
                    };
                    tx.Require(Condition.KeyNotExists(leaseKey));
                    tx.Queue(t => t.StringSetAsync(leaseKey, token));
                    tx.Queue(t => t.KeyExpireAsync(leaseKey,
                        DateTimeOffset.FromUnixTimeMilliseconds(claimed.LeaseUntil).UtcDateTime));
                    if (permit is not null)
                        tx.Queue(t => t.KeyExpireAsync(permit.Key,
                            DateTimeOffset.FromUnixTimeMilliseconds(claimed.LeaseUntil).UtcDateTime));
                    if (rate is not null)
                    {
                        byte[] updated = Serialize(rate with { Count = rate.Count + 1 });
                        tx.Queue(t => t.HashSetAsync(Rates, job.Name, updated));
                    }

                    Save(mutation, claimed, job);
                    tx.Queue(t => t.SortedSetRemoveAsync(Due, job.Id));
                    tx.Queue(t => t.SortedSetAddAsync(Running, job.Id, claimed.LeaseUntil));
                    // On an unknown commit outcome retain the permit until expiry: releasing it might admit another worker.
                    cancellationToken.ThrowIfCancellationRequested();
                    dispatched = true;
                    if (await tx.Execute(cancellationToken))
                        return new(claimed, token, claimed.Version);
                    dispatched = false;
                    conflict = true;
                }
                finally
                {
                    if (!dispatched && permit is not null)
                        await RedisSemaphore.ReleasePermit(db, permit);
                }

                break;
            }

            if (!conflict)
                return null;
            await Retry(attempt, cancellationToken);
        }
    }

    private static bool Owned(JobRecord? job, JobLease lease, long now) => job is not null &&
                                                                           job.State == JobState.Running &&
                                                                           job.Token == lease.Token &&
                                                                           job.Version == lease.Version &&
                                                                           job.LeaseUntil > now;

    private async Task<Ownership> ReadOwnership(IDatabase db, JobLease lease, CancellationToken ct)
    {
        RedisKey leaseKey = LeaseKey(lease.Job.Id);
        Task<RedisValue> jobTask = db.HashGetAsync(Jobs, lease.Job.Id);
        Task<RedisValue> tokenTask = db.StringGetAsync(leaseKey);
        Task<RedisValue> permitTask = db.HashGetAsync(Permits, lease.Job.Id);
        await Task.WhenAll(jobTask, tokenTask, permitTask).WaitAsync(ct);
        if (tokenTask.Result != lease.Token)
            return default;
        JobRecord? job = Decode<JobRecord>(jobTask.Result);
        RedisSemaphorePermit? permit = Decode<StoredPermit>(permitTask.Result)?.ToPermit();
        if (permit is not null && await db.StringGetAsync(permit.Key).WaitAsync(ct) != permit.Token)
            return default;
        return new(job, jobTask.Result, leaseKey, permit);
    }

    private static void RequireOwnership(Mutation mutation, JobLease lease, Ownership ownership)
    {
        mutation.Transaction.Require(Condition.StringEqual(ownership.LeaseKey, lease.Token));
        if (ownership.Permit is not null)
            mutation.Transaction.Require(ownership.Permit.OwnershipCondition);
    }

    private void Release(Mutation mutation, string id, Ownership ownership)
    {
        if (ownership.Permit is not null)
            mutation.Transaction.Queue(t => t.KeyDeleteAsync(ownership.Permit.Key));
        mutation.Transaction.Queue(t => t.HashDeleteAsync(Permits, id));
        mutation.Transaction.Queue(t => t.KeyDeleteAsync(ownership.LeaseKey));
        mutation.Transaction.Queue(t => t.SortedSetRemoveAsync(Running, id));
    }

    public async Task<LeaseStatus> Renew(JobLease lease, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        long milliseconds = Duration(duration);
        IDatabase db = await Database(cancellationToken);
        for (var attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, cancellationToken);
            Ownership ownership = await ReadOwnership(db, lease, cancellationToken);
            JobRecord? job = ownership.Job;
            if (!Owned(job, lease, mutation.Now))
                return LeaseStatus.Lost;
            RequireOwnership(mutation, lease, ownership);
            JobRecord renewed = job! with { LeaseUntil = mutation.Now + milliseconds };
            Save(mutation, renewed, job);
            RedisAtomicTransaction tx = mutation.Transaction;
            DateTime expiration = DateTimeOffset.FromUnixTimeMilliseconds(renewed.LeaseUntil).UtcDateTime;
            tx.Queue(t => t.KeyExpireAsync(ownership.LeaseKey, expiration));
            if (ownership.Permit is not null)
                tx.Queue(t => t.KeyExpireAsync(ownership.Permit.Key, expiration));
            tx.Queue(t => t.SortedSetAddAsync(Running, job!.Id, renewed.LeaseUntil));
            if (await tx.Execute(cancellationToken))
                return job!.CancelRequested ? LeaseStatus.CancellationRequested : LeaseStatus.Renewed;
            await Retry(attempt, cancellationToken);
        }
    }

    private async Task Release(IDatabase db, Mutation mutation, JobRecord job, CancellationToken ct)
    {
        RedisAtomicTransaction tx = mutation.Transaction;
        RedisSemaphorePermit? permit = Decode<StoredPermit>(await db.HashGetAsync(Permits, job.Id).WaitAsync(ct))?.ToPermit();
        if (permit is not null)
        {
            RedisValue value = await db.StringGetAsync(permit.Key).WaitAsync(ct);
            if (value == permit.Token)
            {
                tx.Require(permit.OwnershipCondition);
                tx.Queue(t => t.KeyDeleteAsync(permit.Key));
            }
        }

        tx.Queue(t => t.HashDeleteAsync(Permits, job.Id));
        tx.Queue(t => t.KeyDeleteAsync(LeaseKey(job.Id)));
        tx.Queue(t => t.SortedSetRemoveAsync(Running, job.Id));
    }

    public async Task<bool> Finish(JobLease lease, JobOutcome outcome, string? error, TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        if (!JobOutcome.IsDefined(outcome.Value) || retryDelay < TimeSpan.Zero || retryDelay > TimeSpan.FromDays(30))
            throw new ArgumentOutOfRangeException();
        IDatabase db = await Database(cancellationToken);
        for (var attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, cancellationToken);
            Ownership ownership = await ReadOwnership(db, lease, cancellationToken);
            JobRecord? job = ownership.Job;
            if (!Owned(job, lease, mutation.Now))
                return false;
            RequireOwnership(mutation, lease, ownership);
            JobState state = job!.CancelRequested || outcome == JobOutcome.Cancelled ? JobState.Cancelled :
                outcome == JobOutcome.Succeeded ? JobState.Succeeded :
                job.Attempt >= job.Policy.MaxAttempts ? JobState.DeadLettered : JobState.Scheduled;
            JobRecord finished = job with
            {
                State = state, Token = null, Owner = null, LeaseUntil = 0, Version = job.Version + 1,
                Error = error is null ? "" : error[..Math.Min(error.Length, 1024)],
                Progress = outcome == JobOutcome.Succeeded && job.Progress is not null ? 100 : job.Progress,
                DueAt = state == JobState.Scheduled ? mutation.Now + (long)retryDelay.TotalMilliseconds : job.DueAt
            };
            Release(mutation, job.Id, ownership);
            Save(mutation, finished, job);
            await AdvanceChain(db, mutation, finished, cancellationToken);
            if (state == JobState.Scheduled)
                mutation.Transaction.Queue(t => t.SortedSetAddAsync(Due, job.Id, finished.DueAt));
            if (await mutation.Transaction.Execute(cancellationToken))
                return true;
            await Retry(attempt, cancellationToken);
        }
    }

    public async Task<bool> Cancel(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
            throw new ArgumentException("Invalid job ID.", nameof(id));
        IDatabase db = await Database(cancellationToken);
        for (var attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, cancellationToken);
            var job = Decode<JobRecord>(await db.HashGetAsync(Jobs, id).WaitAsync(cancellationToken));
            if (job is null || (job.State != JobState.Scheduled && job.State != JobState.Running &&
                                job.State != JobState.Waiting))
                return false;
            JobRecord cancelled = job with { CancelRequested = true };
            if (job.State == JobState.Scheduled || job.State == JobState.Waiting)
            {
                cancelled = cancelled with { State = JobState.Cancelled, Version = job.Version + 1 };
                mutation.Transaction.Queue(t => t.SortedSetRemoveAsync(Due, id));
            }

            Save(mutation, cancelled, job);
            if (cancelled.State == JobState.Cancelled)
                await AdvanceChain(db, mutation, cancelled, cancellationToken);
            if (await mutation.Transaction.Execute(cancellationToken))
                return true;
            await Retry(attempt, cancellationToken);
        }
    }
}
