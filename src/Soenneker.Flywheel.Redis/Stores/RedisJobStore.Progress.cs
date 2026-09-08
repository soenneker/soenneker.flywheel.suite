using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Redis.Util.Atomics;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    public async Task<bool> SetProgress(JobLease lease, double percentage, string? message,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(percentage) || percentage is < 0 or > 100 || message?.Length > 500)
            throw new ArgumentOutOfRangeException();
        IDatabase db = await Database(cancellationToken);
        for (var attempt = 0;; attempt++)
        {
            Task<long> timeTask = Time(db, cancellationToken);
            Task<Ownership> ownershipTask = ReadOwnership(db, lease, cancellationToken);
            await Task.WhenAll(timeTask, ownershipTask).WaitAsync(cancellationToken);
            Ownership ownership = ownershipTask.Result;
            JobRecord? job = ownership.Job;
            if (!Owned(job, lease, timeTask.Result)) return false;
            var transaction = new RedisAtomicTransaction(db);
            transaction.Require(RedisAtomics.HashMatches(Jobs, lease.Job.Id, ownership.Raw));
            var mutation = new Mutation(transaction, timeTask.Result, ChangeChannel(db));
            RequireOwnership(mutation, lease, ownership);
            JobRecord updated = job! with
            {
                Progress = percentage,
                ProgressMessage = message,
                ProgressUpdatedAt = mutation.Now,
                UpdatedAt = mutation.Now
            };
            Save(mutation, updated, job);
            if (await mutation.Transaction.Execute(cancellationToken)) return true;
            await Retry(attempt, cancellationToken);
        }
    }
}
