using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class RecurringCoalescingTests
{
    private static EnqueueRequest Request => new("scan", "{}", new JobPolicy { MaxAttempts = 2 }, TimeSpan.Zero);

    [Test]
    public async Task PendingRunningAndRetryingOccurrencesAreCoalesced(CancellationToken ct)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new(), clock);
        await store.AddRecurring("scan", Request, TimeSpan.FromSeconds(1), ct, coalesceOccurrences: true);
        await store.Maintain(100, ct);
        var first = (await store.List(cancellationToken: ct)).Single();
        long? scheduledFor = first.ScheduledFor;
        clock.Advance(TimeSpan.FromSeconds(10));
        await store.Maintain(100, ct);
        Check(await store.RunRecurring("scan", ct) == first.Id, "Manual run bypassed pending coalescing.");
        JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), ct))!;
        clock.Advance(TimeSpan.FromSeconds(2));
        await store.Maintain(100, ct);
        Check(await store.RunRecurring("scan", ct) == first.Id, "Manual run bypassed running coalescing.");
        await store.Finish(lease, JobOutcome.Failed, "retry", TimeSpan.FromSeconds(10), ct);
        clock.Advance(TimeSpan.FromSeconds(2));
        await store.Maintain(100, ct);
        Check((await store.List(cancellationToken: ct)).Count == 1, "Recurring work accumulated behind a retry.");
        Check((await store.Get(first.Id, ct))!.ScheduledFor == scheduledFor, "Retry lost the original schedule time.");
        await store.Cancel(first.Id, ct);
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.Maintain(100, ct);
        Check((await store.List(cancellationToken: ct)).Count == 2, "Terminal occurrence blocked future runs.");
        var schedule = (await store.ListRecurring(cancellationToken: ct)).Single();
        Check(schedule.CoalesceOccurrences && schedule.CoalescedOccurrences == 5, "Coalescing was not observable.");
    }

    [Test]
    public async Task EnablingCoalescingAccountsForAllOutstandingOccurrences(CancellationToken ct)
    {
        await using var store = new MemoryJobStore(new());
        await store.AddRecurring("scan", Request, TimeSpan.FromHours(1), ct);
        string first = (await store.RunRecurring("scan", ct))!;
        string second = (await store.RunRecurring("scan", ct))!;
        Check(first != second, "Default scheduling no longer permits separate occurrences.");
        await store.Cancel(second, ct); // The last occurrence is terminal, but an earlier one is still unfinished.
        var definition = (await store.GetRecurringDefinition("scan", ct))!.Definition;
        await store.UpdateRecurring("scan", definition with { CoalesceOccurrences = true }, ct);
        Check(await store.RunRecurring("scan", ct) == first, "Coalescing only inspected the most recent occurrence.");
        Check((await store.GetRecurringDefinition("scan", ct))!.Definition.CoalesceOccurrences, "Definition edit lost its policy.");
        await store.Cancel(first, ct);
        Check(await store.RunRecurring("scan", ct) != first, "Completed work remained in the outstanding index.");
    }

    [Test]
    public async Task CronKeepsOriginalDueTimeAndAdvancesWhenCoalesced(CancellationToken ct)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new(), clock);
        await store.AddCron("cron", Request, "* * * * *", cancellationToken: ct, coalesceOccurrences: true);
        long due = (await store.ListRecurring(cancellationToken: ct)).Single().DueAt!.Value;
        clock.Advance(TimeSpan.FromMinutes(5));
        await store.Maintain(100, ct);
        JobRecord first = (await store.List(cancellationToken: ct)).Single();
        Check(first.ScheduledFor == due && first.DueAt == clock.GetUtcNow().ToUnixTimeMilliseconds(), "Late cron occurrence lost its intended time.");
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.Maintain(100, ct);
        Check((await store.List(cancellationToken: ct)).Count == 1 && (await store.ListRecurring(cancellationToken: ct)).Single().DueAt > clock.GetUtcNow().ToUnixTimeMilliseconds(),
            "Coalesced cron did not advance its next occurrence.");
    }

    [Test]
    public async Task ChangingCoalescingPreservesPausedStateAndNextRun(CancellationToken ct)
    {
        await using var store = new MemoryJobStore(new());
        await store.AddRecurring("scan", Request, TimeSpan.FromHours(1), ct);
        var before = (await store.ListRecurring(cancellationToken: ct)).Single();
        Check(await store.SetRecurringCoalescing("scan", true, before.Revision, ct), "Coalescing update failed.");
        var after = (await store.ListRecurring(cancellationToken: ct)).Single();
        Check(after.DueAt == before.DueAt && after.Revision == before.Revision + 1, "Policy update moved the schedule.");
        Check(!await store.SetRecurringCoalescing("scan", false, before.Revision, ct), "Stale editor overwrote coalescing.");
        await store.SetRecurringEnabled("scan", false, cancellationToken: ct);
        await store.SetRecurringCoalescing("scan", false, cancellationToken: ct);
        Check(!(await store.ListRecurring(cancellationToken: ct)).Single().Enabled, "Policy change resumed a paused schedule.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
