using Soenneker.Flywheel.Core.Services;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class ReliabilityTests
{
    [Test]
    public async Task Repeated_pause_resume_waits_for_retiring_handlers(CancellationToken cancellationToken)
    {
        await using var fixture = new ReliabilityFixture();
        var store = fixture.Resolve<MemoryJobStore>();
        var job = fixture.Resolve<ReliabilityBlockingJob>();
        var workers = fixture.Resolve<WorkerService>();
        for (int i = 0; i < 6; i++) await store.Enqueue(new(job.Name, "{}", new(), TimeSpan.Zero), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await workers.StartAsync(deadline.Token);
        try
        {
            while (Volatile.Read(ref job.Started) < 2) await Task.Delay(10, deadline.Token);
            for (int i = 0; i < 3; i++)
            {
                await workers.SetWorkerCount(0, deadline.Token);
                await workers.SetWorkerCount(2, deadline.Token);
            }
            await Task.Delay(200, deadline.Token);
            if (Volatile.Read(ref job.Started) != 2) throw new Exception("Retiring handlers were excluded from the hard capacity limit.");
            job.Release.TrySetResult();
            while (Volatile.Read(ref job.Started) < 6) await Task.Delay(10, deadline.Token);
        }
        finally { job.Release.TrySetResult(); await workers.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task Abandoned_instances_expire_but_live_nodes_and_leases_survive(CancellationToken cancellationToken)
    {
        await using var fixture = new ReliabilityFixture();
        var store = fixture.Resolve<MemoryJobStore>();
        EnqueueRequest request = new("startup", "{}", new(), TimeSpan.Zero);
        string abandoned = await store.EnqueueForCurrentInstance(request, "build", "gone", cancellationToken);
        string alive = await store.EnqueueForCurrentInstance(request, "build", "alive", cancellationToken);
        string running = await store.EnqueueForCurrentInstance(request, "build", "running", cancellationToken);
        await store.Heartbeat("alive", 1, TimeSpan.FromMinutes(20), cancellationToken);
        await store.ClaimForWorker("running", TimeSpan.FromMinutes(20), "build", new HashSet<string> { "startup" }, cancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        for (int i = 0; i < 3; i++) await store.Maintain(1, cancellationToken);
        if ((await store.Get(abandoned, cancellationToken))?.State != JobState.DeadLettered ||
            (await store.Get(alive, cancellationToken))?.State != JobState.Scheduled ||
            (await store.Get(running, cancellationToken))?.State != JobState.Running)
            throw new Exception("Abandoned-instance recovery ignored live ownership or retained orphaned work.");
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        await store.Maintain(100, cancellationToken);
        if (await store.Get(abandoned, cancellationToken) is not null) throw new Exception("Abandoned work did not enter normal retention.");
    }

    [Test]
    public async Task Search_crosses_bounded_pages_and_returns_full_selected_records(CancellationToken cancellationToken)
    {
        await using var fixture = new ReliabilityFixture();
        var store = fixture.Resolve<MemoryJobStore>();
        string payload = "{\"text\":\"" + new string('x', 4096) + "\"}";
        for (int i = 0; i < 450; i++)
            await store.Enqueue(new("search", payload, new(), TimeSpan.Zero), cancellationToken);
        var first = await store.Search("search", 0, 200, cancellationToken);
        var second = await store.Search("search", 200, 200, cancellationToken);
        var third = await store.Search("search", 400, 200, cancellationToken);
        if (first.TotalCount != 450 || second.TotalCount != 450 || third.TotalCount != 450 ||
            first.Items.Concat(second.Items).Concat(third.Items).Select(j => j.Id).Distinct().Count() != 450 ||
            first.Items.Any(j => j.Payload != payload))
            throw new Exception("Bounded search lost records, totals, or selected payloads.");
        var lease = (await store.Claim("node", TimeSpan.FromMinutes(1), cancellationToken))!;
        Task search = store.Search("search", cancellationToken: cancellationToken);
        await store.Renew(lease, TimeSpan.FromMinutes(1), cancellationToken);
        await search;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await store.Search("search", cancellationToken: cancelled.Token); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Search ignored cancellation.");
    }
}
