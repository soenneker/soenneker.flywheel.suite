using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

public abstract partial class LibrarianJobStore
{
    public Task<bool> RecordLeaseLoss(JobLease lease, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return Mutate(cancellationToken, async now =>
        {
            JobRecord? job = await _jobs.Get(lease.Job.Id);
            if (job is null || job.State != JobState.Running || job.Token != lease.Token || job.Version != lease.Version)
                return false;
            await Save(job with { LeaseLossReason = reason[..Math.Min(reason.Length, 1024)] }, now, job);
            return true;
        });
    }
}
