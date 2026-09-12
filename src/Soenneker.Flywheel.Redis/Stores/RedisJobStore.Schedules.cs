using Soenneker.Cron.Parser;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    public async Task<bool> AddCron(string id, EnqueueRequest request, string expression, string timeZoneId = "UTC",
        bool includeSeconds = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || request.IdempotencyKey is not null || request.Delay != TimeSpan.Zero)
            throw new ArgumentException("Invalid cron schedule.");
        CronSchedule cron = CronParser.Parse(expression, timeZoneId, includeSeconds);
        var schedule = new Schedule(Create(request, ""), 0, Cron: expression, TimeZoneId: timeZoneId, IncludeSeconds: includeSeconds);
        string key = Key(id);
        IDatabase db = await Database(cancellationToken);
        for (int attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, cancellationToken);
            if (await db.HashExistsAsync(Schedules, key).WaitAsync(cancellationToken)) return false;
            DateTimeOffset next = cron.Next(DateTimeOffset.FromUnixTimeMilliseconds(mutation.Now))
                                  ?? throw new ArgumentException("Cron expression has no future occurrence.", nameof(expression));
            mutation.Transaction.Queue(t => t.HashSetAsync(Schedules, key, Serialize(schedule)));
            mutation.Transaction.Queue(t => t.SortedSetAddAsync(ScheduleDue, key, next.ToUnixTimeMilliseconds()));
            Publish(mutation, new JobChange("Schedules"));
            if (await mutation.Transaction.Execute(cancellationToken)) return true;
            await Retry(attempt, cancellationToken);
        }
    }

    public async Task<bool> AddRecurring(string id, EnqueueRequest request, TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || request.IdempotencyKey is not null ||
            interval < TimeSpan.FromSeconds(1))
            throw new ArgumentException("Invalid recurring schedule.");
        var schedule = new Schedule(Create(request, ""), Duration(interval));
        string key = Key(id);
        IDatabase db = await Database(cancellationToken);
        for (var attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, cancellationToken);
            if (await db.HashExistsAsync(Schedules, key).WaitAsync(cancellationToken))
                return false;
            Publish(mutation, new JobChange("Schedules"));
            mutation.Transaction.Queue(t => t.HashSetAsync(Schedules, key, Serialize(schedule)));
            mutation.Transaction.Queue(t => t.SortedSetAddAsync(ScheduleDue, key, mutation.Now));
            if (await mutation.Transaction.Execute(cancellationToken))
                return true;
            await Retry(attempt, cancellationToken);
        }
    }

    private static JobRecord Occurrence(Schedule schedule, string id) => schedule.Job with
    {
        Id = id, State = JobState.Scheduled, Attempt = 0, Version = 0, Token = null,
        Owner = null, LeaseUntil = 0, CancelRequested = false, Error = null, StartedAt = 0, CompletedAt = 0
    };

    public async Task<string?> RunRecurring(string scheduleId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scheduleId) || scheduleId.Length > 200)
            throw new ArgumentException("Invalid schedule ID.", nameof(scheduleId));
        var id = Guid.NewGuid().ToString("N");
        IDatabase db = await Database(cancellationToken);
        for (var attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, cancellationToken);
            var schedule = Decode<Schedule>(await db.HashGetAsync(Schedules, scheduleId).WaitAsync(cancellationToken));
            if (schedule is null)
                return null;
            Insert(mutation, Occurrence(schedule, id) with { ScheduleId = scheduleId }, 0);
            schedule = schedule with { LastExecutionId = id, LastExecutionStatus = null };
            mutation.Transaction.Queue(t => t.HashSetAsync(Schedules, scheduleId, Serialize(schedule)));
            Publish(mutation, new JobChange("Schedules"));
            if (await mutation.Transaction.Execute(cancellationToken))
                return id;
            await Retry(attempt, cancellationToken);
        }
    }

    public async Task<long> GetRecurringCount(CancellationToken cancellationToken = default)
    {
        IDatabase db = await Database(cancellationToken);
        return await db.SortedSetLengthAsync(ScheduleDue).WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecurringJobView>> ListRecurring(int count = 50,
        CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(count));
        IDatabase db = await Database(cancellationToken);
        SortedSetEntry[] entries = await db.SortedSetRangeByRankWithScoresAsync(ScheduleDue, 0, count - 1)
                                           .WaitAsync(cancellationToken);
        if (entries.Length == 0)
            return [];
        var ids = new RedisValue[entries.Length];
        for (var i = 0; i < entries.Length; i++)
            ids[i] = entries[i].Element;
        RedisValue[] values = await db.HashGetAsync(Schedules, ids)
                                      .WaitAsync(cancellationToken);
        Schedule?[] schedules = values.Select(Decode<Schedule>).ToArray();
        RedisValue[] executionIds = schedules.Select(s => (RedisValue)(s?.LastExecutionId ?? "")).ToArray();
        RedisValue[] executions = await db.HashGetAsync(Jobs, executionIds).WaitAsync(cancellationToken);
        long now = await Time(db, cancellationToken);
        var result = new List<RecurringJobView>();
        for (var i = 0; i < entries.Length; i++)
        {
            var schedule = schedules[i];
            var execution = Decode<JobRecord>(executions[i]);
            string? status = execution is null ? schedule?.LastExecutionStatus :
                execution.CancelRequested && execution.State == JobState.Running ? "Cancelling" : execution.DisplayState(now);
            if (schedule is not null)
                result.Add(new((string)entries[i].Element!, schedule.Job.Name, schedule.Interval,
                    (long)entries[i].Score, schedule.Cron, schedule.TimeZoneId, schedule.IncludeSeconds, status, schedule.LastExecutionId));
        }

        return result;
    }

    public async Task Maintain(int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        IDatabase db = await Database(cancellationToken);
        long now = await Time(db, cancellationToken);
        Task<RedisValue[]> expiredTask = db.SortedSetRangeByScoreAsync(Running, stop: now, take: batchSize);
        Task<RedisValue[]> schedulesTask = db.SortedSetRangeByScoreAsync(ScheduleDue, stop: now, take: batchSize);
        Task<RedisValue[]> historyTask = db.SortedSetRangeByScoreAsync(HistoryBuckets,
            stop: now / 300000 * 300000 - (long)HistoryRetention.TotalMilliseconds - 1, take: batchSize);
        Task<RedisValue[]> completedTask = db.SortedSetRangeByScoreAsync(Completed,
            stop: _retainCompletedJobs ? now - HistoryRetention.TotalMilliseconds : double.PositiveInfinity, take: batchSize);
        await Task.WhenAll(expiredTask, schedulesTask, historyTask, completedTask).WaitAsync(cancellationToken);
        RedisValue[] expired = expiredTask.Result;
        foreach (RedisValue id in expired)
            await Recover(db, (string)id!, cancellationToken);
        RedisValue[] schedules = schedulesTask.Result;
        foreach (RedisValue id in schedules)
            await Materialize(db, (string)id!, cancellationToken);
        if (historyTask.Result.Length > 0)
            await PruneHistory(db, batchSize, cancellationToken);
        // Recovery can create terminal jobs during this pass. Immediate cleanup still includes those jobs.
        RedisValue[] completed = !_retainCompletedJobs && expired.Length > 0
            ? await db.SortedSetRangeByScoreAsync(Completed, take: batchSize).WaitAsync(cancellationToken)
            : completedTask.Result;
        await PruneCompletedJobs(db, completed, cancellationToken);
    }

    private async Task Recover(IDatabase db, string id, CancellationToken ct)
    {
        for (var attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, ct);
            var job = Decode<JobRecord>(await db.HashGetAsync(Jobs, id).WaitAsync(ct));
            if (job is null || job.State != JobState.Running || job.LeaseUntil > mutation.Now)
                return;
            if (await db.KeyExistsAsync(LeaseKey(id)).WaitAsync(ct))
                return;
            mutation.Transaction.Require(Condition.KeyNotExists(LeaseKey(id)));
            JobState state = job.CancelRequested ? JobState.Cancelled :
                job.Attempt >= job.Policy.MaxAttempts ? JobState.DeadLettered : JobState.Scheduled;
            var delay = (long)Math.Min(job.Policy.MaxBackoff.TotalMilliseconds,
                job.Policy.InitialBackoff.TotalMilliseconds * Math.Pow(2, Math.Min(job.Attempt - 1, 30)));
            JobRecord recovered = job with
            {
                State = state, Token = null, Owner = null, LeaseUntil = 0, Version = job.Version + 1,
                Error = "Lease expired", DueAt = state == JobState.Scheduled ? mutation.Now + delay : job.DueAt
            };
            await Release(db, mutation, job, ct);
            Save(mutation, recovered, job);
            await AdvanceChain(db, mutation, recovered, ct);
            if (state == JobState.Scheduled)
                mutation.Transaction.Queue(t => t.SortedSetAddAsync(Due, id, recovered.DueAt));
            if (await mutation.Transaction.Execute(ct))
                return;
            await Retry(attempt, ct);
        }
    }

    private async Task Materialize(IDatabase db, string id, CancellationToken ct)
    {
        for (var attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, ct);
            double? due = await db.SortedSetScoreAsync(ScheduleDue, id).WaitAsync(ct);
            if (due is null || due > mutation.Now)
                return;
            var schedule = Decode<Schedule>(await db.HashGetAsync(Schedules, id).WaitAsync(ct));
            if (schedule is null)
                return;
            string executionId = Guid.NewGuid().ToString("N");
            schedule = schedule with { Sequence = checked(schedule.Sequence + 1), LastExecutionId = executionId, LastExecutionStatus = null };
            Insert(mutation, Occurrence(schedule, executionId) with { ScheduleId = id }, 0);
            long? next = schedule.Cron is null
                ? checked((long)due + ((mutation.Now - (long)due) / schedule.Interval + 1) * schedule.Interval)
                : CronParser.Parse(schedule.Cron, schedule.TimeZoneId, schedule.IncludeSeconds)
                    .Next(DateTimeOffset.FromUnixTimeMilliseconds(mutation.Now))?.ToUnixTimeMilliseconds();
            mutation.Transaction.Queue(t => t.HashSetAsync(Schedules, id, Serialize(schedule)));
            if (next is { } nextTime)
                mutation.Transaction.Queue(t => t.SortedSetAddAsync(ScheduleDue, id, nextTime));
            else
                mutation.Transaction.Queue(t => t.SortedSetRemoveAsync(ScheduleDue, id));
            Publish(mutation, new JobChange("Schedules"));
            if (await mutation.Transaction.Execute(ct))
                return;
            await Retry(attempt, ct);
        }
    }

    public async Task Heartbeat(string node, int workers, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(node) || node.Length > 200)
            throw new ArgumentException("Invalid server ID.", nameof(node));
        if (workers is < 0 or > 256)
            throw new ArgumentOutOfRangeException(nameof(workers));
        long milliseconds = Duration(ttl);
        IDatabase db = await Database(cancellationToken);
        await db.ScriptEvaluateAsync(HeartbeatScript, _heartbeatKeys,
            [node, workers, milliseconds, ChangeChannel(db).ToString(), Serialize(new JobChange("Servers", node))])
            .WaitAsync(cancellationToken);
    }

    // Server liveness is independent of job selection. Renewals must not invalidate dispatch snapshots or
    // rebuild dashboard boards unless membership/capacity changed. Prune before inserting a returning node.
    private const string HeartbeatScript = """
        local clock = redis.call('TIME')
        local now = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
        local previous = redis.call('HGET', KEYS[2], ARGV[1])
        local expired = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', now)
        for i = 1, #expired, 256 do
            redis.call('HDEL', KEYS[2], unpack(expired, i, math.min(i + 255, #expired)))
        end
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        redis.call('ZADD', KEYS[1], now + tonumber(ARGV[3]), ARGV[1])
        redis.call('HSET', KEYS[2], ARGV[1], ARGV[2])
        if #expired > 0 or previous ~= ARGV[2] then redis.call('PUBLISH', ARGV[4], ARGV[5]) end
        return 1
        """;

    private async Task PruneHistory(IDatabase db, int batchSize, CancellationToken ct)
    {
        for (var attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, ct);
            RedisValue[] expired = await db.SortedSetRangeByScoreAsync(HistoryBuckets,
                stop: mutation.Now / 300000 * 300000 - (long)HistoryRetention.TotalMilliseconds - 1, take: batchSize).WaitAsync(ct);
            if (expired.Length == 0)
                return;
            RedisValue[] fields = BuildHistoryFields(expired);
            mutation.Transaction.Queue(t => t.HashDeleteAsync(History, fields));
            mutation.Transaction.Queue(t => t.SortedSetRemoveAsync(HistoryBuckets, expired));
            if (await mutation.Transaction.Execute(ct))
                return;
            await Retry(attempt, ct);
        }
    }


    private async Task PruneCompletedJobs(IDatabase db, RedisValue[] ids, CancellationToken ct)
    {
        foreach (RedisValue value in ids)
        {
            string id = (string)value!;
            for (var attempt = 0;; attempt++)
            {
                Mutation mutation = await Begin(db, ct);
                var job = Decode<JobRecord>(await db.HashGetAsync(Jobs, id).WaitAsync(ct));
                if (job is not null && !IsTerminal(job.State)) break;
                if (job?.ScheduleId is { } scheduleId)
                {
                    var schedule = Decode<Schedule>(await db.HashGetAsync(Schedules, scheduleId).WaitAsync(ct));
                    if (schedule?.LastExecutionId == id)
                    {
                        schedule = schedule with { LastExecutionStatus = job.DisplayState(mutation.Now) };
                        mutation.Transaction.Queue(t => t.HashSetAsync(Schedules, scheduleId, Serialize(schedule)));
                    }
                }
                RedisValue reverse = await db.HashGetAsync(DedupeReverse, id).WaitAsync(ct);
                if (!reverse.IsNull)
                {
                    string mapping = (string)reverse!;
                    if (mapping.Length > 2)
                        mutation.Transaction.Queue(t => t.HashDeleteAsync(mapping[0] == 'c' ? ChainDedupe : Dedupe, mapping[2..]));
                }
                mutation.Transaction.Queue(t => t.HashDeleteAsync(Jobs, id));
                mutation.Transaction.Queue(t => t.HashDeleteAsync(Dispatch, id));
                mutation.Transaction.Queue(t => t.HashDeleteAsync(DedupeReverse, id));
                mutation.Transaction.Queue(t => t.SortedSetRemoveAsync(All, id));
                mutation.Transaction.Queue(t => t.SortedSetRemoveAsync(Completed, id));
                mutation.Transaction.Queue(t => t.KeyDeleteAsync(LogKey(id)));
                Publish(mutation, new JobChange("Job", id));
                if (await mutation.Transaction.Execute(ct)) break;
                await Retry(attempt, ct);
            }
        }
    }

    private static RedisValue[] BuildHistoryFields(RedisValue[] expired)
    {
        var fields = new RedisValue[checked(expired.Length * 6)];
        int index = 0;
        foreach (RedisValue value in expired)
        {
            string stamp = value.ToString();
            for (var state = 0; state < 6; state++)
                fields[index++] = $"{stamp}:{state}";
        }
        return fields;
    }

}
