using Soenneker.Extensions.ValueTask;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    public Task<bool> UpdateQueue(string id, JobQueueAction action, CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        return Mutate(cancellationToken, async now =>
        {
            JobRecord? job = await _jobs.Get(id);
            if (job is null || job.State != JobState.Scheduled || job.CancelRequested ||
                action != JobQueueAction.RunNow && job.DueAt > now) return false;

            long rank;
            if (action == JobQueueAction.MoveToTop)
            {
                rank = checked(await _metadata.Get("queueFront") - 1);
                await _metadata.Set("queueFront", rank).NoSync();
            }
            else if (action == JobQueueAction.MoveToBottom)
            {
                // Existing ordinary jobs have the current back rank. Reserve the next rank for this
                // move and the following rank for new arrivals, so arrivals cannot starve moved work.
                rank = checked(await _metadata.Get("queueBack") + 1);
                await _metadata.Set("queueBack", checked(rank + 1)).NoSync();
            }
            else rank = await _metadata.Get("queueBack");

            await Save(job with { QueueRank = rank, DueAt = action == JobQueueAction.RunNow ? now : job.DueAt }, now, job);
            return true;
        });
    }
}
