using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Dtos;
using StackExchange.Redis;
using Soenneker.Redis.Util.Atomics;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    public async Task<bool> AppendLogs(JobLease lease, IReadOnlyList<JobLogMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count is < 1 or > 50 ||
            messages.Any(m => m.Message.Length > 4096 || m.Category.Length > 200 || m.Level.Length > 20))
            throw new ArgumentOutOfRangeException(nameof(messages));
        IDatabase db = await Database(cancellationToken);
        RedisKey logKey = LogKey(lease.Job.Id);
        for (var attempt = 0; ; attempt++)
        {
            Task<long> timeTask = Time(db, cancellationToken);
            Task<Ownership> ownershipTask = ReadOwnership(db, lease, cancellationToken);
            await Task.WhenAll(timeTask, ownershipTask).WaitAsync(cancellationToken);
            Ownership ownership = ownershipTask.Result;
            JobRecord? job = ownership.Job;
            if (!Owned(job, lease, timeTask.Result)) return false;
            // Logs do not change dispatch state. Fence this job directly without invalidating every worker's selection.
            var transaction = new RedisAtomicTransaction(db);
            transaction.Require(RedisAtomics.HashMatches(Jobs, lease.Job.Id, ownership.Raw));
            var mutation = new Mutation(transaction, timeTask.Result, ChangeChannel(db));
            RequireOwnership(mutation, lease, ownership);
            foreach (JobLogMessage message in messages)
            {
                NameValueEntry[] fields =
                [
                    new("time", mutation.Now), new("attempt", job!.Attempt), new("level", message.Level),
                    new("category", message.Category), new("message", message.Message)
                ];
                mutation.Transaction.Queue(t =>
                    t.StreamAddAsync(logKey, fields, maxLength: 1000, useApproximateMaxLength: false));
            }

            TimeSpan logRetention = _retainCompletedJobs && HistoryRetention > TimeSpan.FromDays(30)
                ? HistoryRetention
                : TimeSpan.FromDays(30);
            mutation.Transaction.Queue(t => t.KeyExpireAsync(logKey, logRetention));
            Publish(mutation, new JobChange("Logs", lease.Job.Id));
            if (await mutation.Transaction.Execute(cancellationToken))
                return true;
            await Retry(attempt, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<JobLogEntry>> GetLogs(string jobId, int count = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Length > 200)
            throw new ArgumentException("Invalid job ID.", nameof(jobId));
        if (count is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(count));
        IDatabase db = await Database(cancellationToken);
        StreamEntry[] entries = await db.StreamRangeAsync(LogKey(jobId), "-", "+", count, Order.Descending)
                                        .WaitAsync(cancellationToken);
        var result = new JobLogEntry[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            StreamEntry entry = entries[entries.Length - i - 1];
            long timestamp = 0;
            int attempt = 0;
            string level = "", category = "", message = "";
            foreach (NameValueEntry field in entry.Values)
            {
                if (field.Name == "time") timestamp = (long)field.Value;
                else if (field.Name == "attempt") attempt = (int)field.Value;
                else if (field.Name == "level") level = (string)field.Value!;
                else if (field.Name == "category") category = (string)field.Value!;
                else if (field.Name == "message") message = (string)field.Value!;
            }
            result[i] = new(entry.Id!, timestamp, attempt, level, category, message);
        }
        return result;
    }
}
