using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Core.Services;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Librarian.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class DebouncedJobTests
{
    private static readonly JobDefinition<string> Job = new("debounce-test");
    private static JobClient Client(IJobStore store) => new(store, [new DebounceTestInvoker()]);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public async Task Latest_submission_resets_due_time_and_replaces_payload()
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        var client = Client(store);
        await client.EnqueueDebounced(Job, "first", "page", TimeSpan.FromMinutes(10));
        string first = (await store.List()).Single().Id;
        clock.Advance(TimeSpan.FromMinutes(6));
        await client.EnqueueDebounced(Job, "latest", "page", TimeSpan.FromMinutes(10));
        Check((await store.Get(first))!.State == JobState.Cancelled, "Previous execution was not cancelled.");
        clock.Advance(TimeSpan.FromMinutes(4));
        Check(await store.Claim("worker", TimeSpan.FromMinutes(1)) is null, "Original deadline still executed.");
        clock.Advance(TimeSpan.FromMinutes(6));
        var lease = await store.Claim("worker", TimeSpan.FromMinutes(1));
        Check(lease?.Job.Payload == "\"latest\"", "Latest payload did not execute at its new deadline.");
        Check(await store.Claim("other", TimeSpan.FromMinutes(1)) is null, "Duplicate execution was queued.");
    }

    [Test]
    public async Task Running_predecessor_is_cancelled_and_other_ids_are_independent()
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        var client = Client(store);
        await client.EnqueueDebounced(Job, "first", "a", TimeSpan.Zero);
        var running = (await store.Claim("worker", TimeSpan.FromMinutes(1)))!;
        await client.EnqueueDebounced(Job, "replacement", "a", TimeSpan.FromMinutes(10));
        Check(await store.Renew(running, TimeSpan.FromMinutes(1)) == LeaseStatus.CancellationRequested,
            "Running execution did not receive cooperative cancellation.");
        await client.EnqueueDebounced(Job, "other", "b", TimeSpan.Zero);
        Check((await store.Claim("other", TimeSpan.FromMinutes(1)))?.Job.Payload == "\"other\"", "Different key was delayed or cancelled.");
    }

    [Test]
    public async Task Duplicate_and_old_deliveries_do_not_reset_the_new_job()
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        var client = Client(store);
        var oldTime = clock.GetUtcNow();
        await client.EnqueueDebounced(Job, "old", "page", "old", oldTime, TimeSpan.FromMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(1));
        var newTime = clock.GetUtcNow();
        await client.EnqueueDebounced(Job, "new", "page", "new", newTime, TimeSpan.FromMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(1));
        await client.EnqueueDebounced(Job, "old", "page", "old", oldTime, TimeSpan.FromMinutes(10));
        await client.EnqueueDebounced(Job, "duplicate", "page", "new", newTime, TimeSpan.FromMinutes(10));
        Check((await store.List()).Count == 2, "Retry created another execution.");
        Check(await client.GetDebouncedRequestId("page") == "new", "Old delivery replaced latest revision.");
        clock.Advance(TimeSpan.FromMinutes(9));
        Check((await store.Claim("worker", TimeSpan.FromMinutes(1)))?.Job.Payload == "\"new\"", "Retry moved the deadline or replaced the payload.");
        bool invoked = false;
        Check(!await client.CommitDebounced("page", "old", _ => { invoked = true; return ValueTask.CompletedTask; }) && !invoked,
            "Stale publication callback ran.");
    }

    [Test]
    public async Task Commit_excludes_replacement_and_releases_lease_on_failure()
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        var client = Client(store);
        await client.EnqueueDebounced(Job, "first", "page", TimeSpan.Zero);
        string? first = await client.GetDebouncedRequestId("page");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commit = client.CommitDebounced("page", first, async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            throw new InvalidOperationException("commit failed");
        }, timeout.Token).AsTask();
        await entered.Task.WaitAsync(timeout.Token);
        var replacement = client.EnqueueDebounced(Job, "new", "page", TimeSpan.Zero, cancellationToken: timeout.Token);
        Check(!replacement.IsCompleted, "Replacement bypassed the active commit lease.");
        release.SetResult();
        try { await commit; throw new Exception("Expected callback failure."); }
        catch (InvalidOperationException ex) when (ex.Message == "commit failed") { }
        await replacement;
        Check(await client.GetDebouncedRequestId("page") != first, "Failed callback left the key locked.");
    }

    [Test]
    public async Task Concurrent_submissions_leave_one_scheduled_execution()
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        await using var store = new DebounceSharedStore(database);
        await using var peer = new DebounceSharedStore(database);
        var client = Client(store);
        var peerClient = Client(peer);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? client : peerClient)
            .EnqueueDebounced(Job, i.ToString(), "page", TimeSpan.FromMinutes(10))));
        var jobs = await store.List();
        Check(jobs.Count(job => job.State == JobState.Scheduled) == 1, "Concurrent submissions left multiple pending jobs.");
        Check(jobs.Count(job => job.State == JobState.Cancelled) == 11, "Superseded executions were not cancelled.");
    }
}
