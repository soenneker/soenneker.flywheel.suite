using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore : IJobQueueCountStore
{
    public Task<JobQueueCounts> GetQueueCounts(CancellationToken cancellationToken = default) =>
        Mutate(cancellationToken, async now =>
        {
            long pending = await _jobs.Count("state", JobState.Scheduled.Value);
            long queued = await _jobs.CountRange("scheduledOrder", minimum: "", maximum: ScheduledUpperBound(now));
            return new JobQueueCounts(queued, pending - queued);
        });
}
