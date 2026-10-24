using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
namespace Soenneker.Flywheel.Memory.Tests;

public sealed class WorkflowTests
{
    private static EnqueueRequest Request(string name = "job", int attempts = 1, TimeSpan? delay = null) =>
        new(name, "{}", new JobPolicy { MaxAttempts = attempts }, delay ?? TimeSpan.Zero);
    private static void Check(bool value, string error) { if (!value) throw new Exception(error); }


    [Test]
    public async Task IndependentWorkersCommitOneBatchAndReleaseOneJoin(CancellationToken ct)
    {
        await using var database = new Soenneker.Librarian.Memory.MemoryLibrarianDatabase(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Soenneker.Librarian.Memory.MemoryLibrarianDatabase>.Instance);
        await using var a = new WorkflowTestStore(database);
        await using var b = new WorkflowTestStore(database);
        var submissions = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => (i % 2 == 0 ? a : b)
            .EnqueueBatch([Request("a"), Request("b")], Request("join"), idempotencyKey: "race", cancellationToken: ct)));
        Check(submissions.Select(s => s.Id).Distinct().Count() == 1, "Independent stores duplicated the batch.");
        var first = (await a.Claim("a", TimeSpan.FromMinutes(1), ct))!;
        var second = (await b.Claim("b", TimeSpan.FromMinutes(1), ct))!;
        await Task.WhenAll(a.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero, ct), b.Finish(second, JobOutcome.Succeeded, null, TimeSpan.Zero, ct));
        var claims = await Task.WhenAll(a.Claim("a", TimeSpan.FromMinutes(1), ct), b.Claim("b", TimeSpan.FromMinutes(1), ct));
        Check(claims.Count(c => c is not null) == 1 && claims.Single(c => c is not null)!.Job.Id == submissions[0].ContinuationId, "Join admission raced.");
    }

    [Test]
    public async Task ActiveWorkflowDoesNotStarveOtherHistoryCleanup(CancellationToken ct)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new() { HistoryRetention = TimeSpan.FromMinutes(5) }, clock);
        var batch = await store.EnqueueBatch([Request()], Request(delay: TimeSpan.FromDays(1)), cancellationToken: ct);
        var first = (await store.Claim("worker", TimeSpan.FromMinutes(1), ct))!;
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero, ct);
        clock.Advance(TimeSpan.FromSeconds(1));
        string other = await store.Enqueue(Request(), ct);
        var lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), ct))!;
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, ct);
        clock.Advance(TimeSpan.FromMinutes(6));
        for (int i = 0; i < 4; i++) await store.Maintain(1, ct);
        Check(await store.Get(other, ct) is null && await store.Get(batch.JobIds[0], ct) is not null, "Protected workflow blocked history pruning.");
    }

    [Test]
    public async Task ConditionalBranchesAttachAfterCompletionAndSkipDescendants(CancellationToken ct)
    {
        await using var store = new MemoryJobStore(new());
        string parent = await store.Enqueue(Request(), ct);
        var lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), ct))!;
        await store.Finish(lease, JobOutcome.Failed, "permanent", TimeSpan.Zero, ct);
        string success = await store.ContinueWith(parent, Request("success"), cancellationToken: ct);
        string failure = await store.ContinueWith(parent, Request("failure"), ContinuationCondition.OnFailure, "failure-key", ct);
        string cleanup = await store.ContinueWith(parent, Request("cleanup"), ContinuationCondition.OnCompleted, cancellationToken: ct);
        string skippedChild = await store.ContinueWith(success, Request("must-not-run"), ContinuationCondition.OnCancelled, cancellationToken: ct);
        Check((await store.Get(success, ct))!.DependencySkipped && (await store.Get(skippedChild, ct))!.DependencySkipped, "Unmatched branch ran.");
        Check(await store.ContinueWith(parent, Request(), ContinuationCondition.OnFailure, "failure-key", ct) == failure, "Continuation dedupe failed.");
        var first = (await store.Claim("worker", TimeSpan.FromMinutes(1), ct))!;
        var second = (await store.Claim("worker2", TimeSpan.FromMinutes(1), ct))!;
        Check(new[] { first.Job.Id, second.Job.Id }.ToHashSet().SetEquals([failure, cleanup]), "Branches not released.");
        Check((await store.GetWorkflow(parent, ct))!.Total == 5, "Workflow membership lost.");
    }

    [Test]
    public async Task ParallelBatchWaitsThroughRetriesAndReleasesFinalStepOnce(CancellationToken ct)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new(), clock);
        var batch = await store.EnqueueBatch([Request("a", 2), Request("b")], Request("final", delay: TimeSpan.FromSeconds(2)), idempotencyKey: "batch", cancellationToken: ct);
        Check((await store.EnqueueBatch([Request()], idempotencyKey: "batch", cancellationToken: ct)).Id == batch.Id, "Batch dedupe failed.");
        var a = (await store.Claim("a", TimeSpan.FromMinutes(1), ct))!;
        var b = (await store.Claim("b", TimeSpan.FromMinutes(1), ct))!;
        if (a.Job.Name == "b") (a, b) = (b, a);
        Check(await store.Claim("c", TimeSpan.FromMinutes(1), ct) is null, "Final step claimed early.");
        await store.Finish(a, JobOutcome.Failed, "retry", TimeSpan.Zero, ct);
        await store.Finish(b, JobOutcome.Succeeded, null, TimeSpan.Zero, ct);
        Check((await store.Get(batch.ContinuationId!, ct))!.State == JobState.Waiting, "Retry released final step.");
        a = (await store.Claim("a", TimeSpan.FromMinutes(1), ct))!;
        await store.Finish(a, JobOutcome.Succeeded, null, TimeSpan.Zero, ct);
        Check(await store.Claim("c", TimeSpan.FromMinutes(1), ct) is null, "Final delay ignored.");
        clock.Advance(TimeSpan.FromSeconds(2));
        var final = (await store.Claim("c", TimeSpan.FromMinutes(1), ct))!;
        Check(final.Job.Id == batch.ContinuationId, "Final step missing.");
        Check(await store.Claim("d", TimeSpan.FromMinutes(1), ct) is null, "Final step claimed twice.");
        await store.Finish(final, JobOutcome.Succeeded, null, TimeSpan.Zero, ct);
        var view = (await store.GetWorkflow(final.Job.Id, ct))!;
        Check(view.Completed == 3 && view.State == "Succeeded", "Aggregate state incorrect.");
    }

    [Test]
    public async Task BatchFailureAndCancellationRespectCompletionCondition(CancellationToken ct)
    {
        foreach (var condition in new[] { ContinuationCondition.OnSuccess, ContinuationCondition.OnCompleted })
        {
            await using var store = new MemoryJobStore(new());
            var batch = await store.EnqueueBatch([Request(), Request()], Request("final"), condition, cancellationToken: ct);
            await store.Cancel(batch.JobIds[0], ct);
            Check((await store.Get(batch.ContinuationId!, ct))!.State == JobState.Waiting, "Final step did not wait for all members.");
            var member = (await store.Claim("worker", TimeSpan.FromMinutes(1), ct))!;
            await store.Finish(member, JobOutcome.Failed, "failure", TimeSpan.Zero, ct);
            var final = (await store.Get(batch.ContinuationId!, ct))!;
            Check(condition == ContinuationCondition.OnSuccess ? final.DependencySkipped : final.State == JobState.Scheduled, "Wrong join condition.");
        }
    }

    [Test]
    public async Task RetentionKeepsEntireWorkflowUntilLastStepFinishes(CancellationToken ct)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new() { HistoryRetention = TimeSpan.FromMinutes(5) }, clock);
        var batch = await store.EnqueueBatch([Request(), Request(delay: TimeSpan.FromHours(1))], Request("final"), cancellationToken: ct);
        var first = (await store.Claim("worker", TimeSpan.FromMinutes(1), ct))!;
        await store.Finish(first, JobOutcome.Succeeded, null, TimeSpan.Zero, ct);
        clock.Advance(TimeSpan.FromMinutes(10));
        await store.Maintain(1, ct);
        Check(await store.Get(first.Job.Id, ct) is not null, "Predecessor expired while workflow waiting.");
        clock.Advance(TimeSpan.FromHours(1));
        var second = (await store.Claim("worker", TimeSpan.FromMinutes(1), ct))!;
        await store.Finish(second, JobOutcome.Succeeded, null, TimeSpan.Zero, ct);
        var final = (await store.Claim("worker", TimeSpan.FromMinutes(1), ct))!;
        await store.Finish(final, JobOutcome.Succeeded, null, TimeSpan.Zero, ct);
        clock.Advance(TimeSpan.FromMinutes(6));
        await store.Maintain(1, ct);
        foreach (string id in batch.JobIds) Check(await store.Get(id, ct) is null, "Workflow retained after expiry.");
    }

    [Test]
    public async Task ParentCompletionRacingAttachmentCannotLoseContinuation(CancellationToken ct)
    {
        await using var store = new MemoryJobStore(new());
        string parent = await store.Enqueue(Request(), ct);
        var lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), ct))!;
        var children = Enumerable.Range(0, 12).Select(_ => store.ContinueWith(parent, Request("child"), idempotencyKey: "same", cancellationToken: ct)).ToArray();
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, ct);
        string[] ids = await Task.WhenAll(children);
        Check(ids.Distinct().Count() == 1, "Concurrent submission duplicated continuation.");
        Check((await store.Claim("child", TimeSpan.FromMinutes(1), ct))!.Job.Id == ids[0], "Continuation lost.");
    }

    [Test]
    public async Task InvalidBatchCommitsNothingAndLeaseRecoveryReleasesFailureBranch(CancellationToken ct)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new(), clock);
        try { await store.EnqueueBatch([Request(), Request() with { Payload = "invalid" }], cancellationToken: ct); throw new Exception("Invalid payload accepted."); }
        catch (System.Text.Json.JsonException) { }
        Check((await store.List(cancellationToken: ct)).Count == 0, "Partial batch persisted.");
        string parent = await store.Enqueue(Request() with { Policy = new JobPolicy { MaxStaleRestarts = 0 } }, ct);
        string child = await store.ContinueWith(parent, Request("failure"), ContinuationCondition.OnFailure, cancellationToken: ct);
        await store.Claim("worker", TimeSpan.FromSeconds(1), ct);
        clock.Advance(TimeSpan.FromSeconds(2));
        await store.Maintain(100, ct);
        Check((await store.Claim("worker", TimeSpan.FromMinutes(1), ct))!.Job.Id == child, "Recovery did not advance workflow.");
    }
}
