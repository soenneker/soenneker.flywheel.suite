using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Dashboard;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class LeaseDiagnosticsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpirationRetainsDiagnosticsWithoutHandlerLogs(bool reported, CancellationToken cancellationToken)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        string id = await store.Enqueue(new EnqueueRequest("job", "{}", new JobPolicy(), TimeSpan.Zero), cancellationToken);
        JobLease lease = (await store.Claim("original-worker", TimeSpan.FromSeconds(30), cancellationToken))!;
        clock.Advance(TimeSpan.FromSeconds(10));
        await store.Renew(lease, TimeSpan.FromSeconds(30), cancellationToken);
        long renewedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
        clock.Advance(TimeSpan.FromSeconds(31));
        if (reported && !await store.RecordLeaseLoss(lease, "Renewal deadline exceeded", cancellationToken))
            throw new Exception("Expired lease diagnostics were rejected before recovery.");
        await store.Maintain(100, cancellationToken);
        JobRecord job = (await store.Get(id, cancellationToken))!;
        var view = new DashboardSnapshotFactory().Job(job);
        if (job.Owner is not null || job.LeaseUntil != 0 || job.Error != "Lease expired" ||
            view.LastOwner != "original-worker" || view.LastLeaseRenewedAt != renewedAt ||
            view.LastLeaseUntil != renewedAt + 30000 ||
            (reported ? view.LeaseLossReason != "Renewal deadline exceeded" : view.LeaseLossReason?.Contains("did not record a cause") != true))
            throw new Exception("Recovery or dashboard projection lost the lease diagnostics.");
        if (await store.RecordLeaseLoss(lease, "late report", cancellationToken))
            throw new Exception("Recovered execution accepted a stale diagnostic report.");
    }

    [Test]
    public async Task NewAttemptResetsDiagnosticsAndRejectsOldWorker(CancellationToken cancellationToken)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        string id = await store.Enqueue(new EnqueueRequest("job", "{}", new JobPolicy { MaxAttempts = 2 }, TimeSpan.Zero), cancellationToken);
        JobLease old = (await store.Claim("old", TimeSpan.FromSeconds(30), cancellationToken))!;
        await store.RecordLeaseLoss(old, "old failure", cancellationToken);
        clock.Advance(TimeSpan.FromSeconds(31));
        await store.Maintain(100, cancellationToken);
        clock.Advance(TimeSpan.FromDays(1));
        JobLease current = (await store.Claim("new", TimeSpan.FromSeconds(30), cancellationToken))!;
        if (await store.RecordLeaseLoss(old, "stale failure", cancellationToken))
            throw new Exception("Old worker overwrote a newer attempt.");
        JobRecord job = (await store.Get(id, cancellationToken))!;
        if (job.LastOwner != "new" || job.LastLeaseRenewedAt != 0 || job.LeaseLossReason is not null ||
            job.LastLeaseUntil != current.Job.LeaseUntil)
            throw new Exception("New attempt retained previous lease diagnostics.");
    }
}
