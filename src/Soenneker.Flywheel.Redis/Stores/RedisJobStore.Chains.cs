using Soenneker.Flywheel.Core.Enums;
using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Requests;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

public sealed partial class RedisJobStore
{
    public async Task<IReadOnlyList<string>> EnqueueChain(IReadOnlyList<EnqueueRequest> steps, string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count is < 1 or > 100 || idempotencyKey?.Length > 256)
            throw new ArgumentException("A chain requires 1–100 steps and a key of at most 256 characters.");
        // Validate and copy everything before connecting or dispatching any writes.
        EnqueueRequest[] requests = steps.ToArray();
        var jobs = new JobRecord[requests.Length];
        for (int i = 0; i < requests.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(requests[i]);
            if (requests[i].IdempotencyKey is not null) throw new ArgumentException("Use a chain key instead of step keys.");
            jobs[i] = Create(requests[i], Guid.NewGuid().ToString("N"));
        }
        for (int i = 0; i < jobs.Length; i++)
            jobs[i] = jobs[i] with
            {
                State = i == 0 ? JobState.Scheduled : JobState.Waiting,
                ParentJobId = i == 0 ? null : jobs[i - 1].Id,
                NextJobId = i + 1 == jobs.Length ? null : jobs[i + 1].Id,
                DelayAfterParent = i == 0 ? 0 : (long)requests[i].Delay.TotalMilliseconds
            };
        string[] ids = jobs.Select(job => job.Id).ToArray();
        string? key = idempotencyKey is null ? null : Key(idempotencyKey);
        IDatabase db = await Database(cancellationToken);
        for (int attempt = 0;; attempt++)
        {
            Mutation mutation = await Begin(db, cancellationToken);
            if (key is not null)
            {
                var existing = Decode<string[]>(await db.HashGetAsync(ChainDedupe, key).WaitAsync(cancellationToken));
                if (existing is not null) return existing;
                mutation.Transaction.Queue(t => t.HashSetAsync(ChainDedupe, key, Serialize(ids)));
                foreach (string id in ids)
                    mutation.Transaction.Queue(t => t.HashSetAsync(DedupeReverse, id, $"c:{key}"));
            }
            for (int i = 0; i < jobs.Length; i++)
                Insert(mutation, jobs[i], i == 0 ? (long)requests[i].Delay.TotalMilliseconds : 0);
            if (await mutation.Transaction.Execute(cancellationToken)) return ids;
            await Retry(attempt, cancellationToken);
        }
    }

    private async Task AdvanceChain(IDatabase db, Mutation mutation, JobRecord parent, CancellationToken ct)
    {
        if (parent.State != JobState.Succeeded && parent.State != JobState.Cancelled && parent.State != JobState.DeadLettered)
            return;
        string? nextId = parent.NextJobId;
        // Chain length is bounded at submission. All changes share the predecessor's fenced transaction.
        for (int i = 0; nextId is not null && i < 100; i++)
        {
            var next = Decode<JobRecord>(await db.HashGetAsync(Jobs, nextId).WaitAsync(ct))
                ?? throw new InvalidOperationException("A persisted chain step is missing.");
            if (next.State != JobState.Waiting) return; // A user may already have cancelled this suffix.
            if (parent.State == JobState.Succeeded)
            {
                var ready = next with { State = JobState.Scheduled, DueAt = checked(mutation.Now + next.DelayAfterParent) };
                Save(mutation, ready, next);
                mutation.Transaction.Queue(t => t.SortedSetAddAsync(Due, ready.Id, ready.DueAt));
                return;
            }
            Save(mutation, next with
            {
                State = JobState.Cancelled, CancelRequested = true, Version = next.Version + 1,
                Error = "Predecessor did not succeed."
            }, next);
            nextId = next.NextJobId;
        }
    }
}
