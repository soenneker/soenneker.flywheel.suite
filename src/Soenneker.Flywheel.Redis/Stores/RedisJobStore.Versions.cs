using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    public async Task<string> RunOnceForCurrentVersion(EnqueueRequest request, string applicationVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateApplicationVersion(applicationVersion);
        if (request.IdempotencyKey is not null)
            throw new ArgumentException("Version-scoped submissions use the job name and application version as their key.", nameof(request));
        JobRecord job = Create(request, Guid.NewGuid().ToString("N")) with { ApplicationVersion = applicationVersion };
        IDatabase db = await Database(cancellationToken);
        RedisKey submissions = _prefix + "version-submissions";
        string key = Key(request.Name) + ":" + Key(applicationVersion);
        for (var attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, cancellationToken);
            RedisValue existing = await db.HashGetAsync(submissions, key).WaitAsync(cancellationToken);
            if (!existing.IsNull) return (string)existing!;
            // Deliberately independent of completed-job retention and ordinary enqueue deduplication.
            mutation.Transaction.Queue(t => t.HashSetAsync(submissions, key, job.Id));
            Insert(mutation, job, (long)request.Delay.TotalMilliseconds);
            if (await mutation.Transaction.Execute(cancellationToken)) return job.Id;
            await Retry(attempt, cancellationToken);
        }
    }

    public Task<JobLease?> ClaimForVersion(string owner, TimeSpan duration, string applicationVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateApplicationVersion(applicationVersion);
        return ClaimCore(owner, duration, applicationVersion, cancellationToken);
    }

    private static void ValidateApplicationVersion(string applicationVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        if (applicationVersion.Length > 200)
            throw new ArgumentOutOfRangeException(nameof(applicationVersion));
    }
}
