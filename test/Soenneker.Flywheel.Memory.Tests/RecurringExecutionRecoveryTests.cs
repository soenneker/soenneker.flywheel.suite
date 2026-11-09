using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Hashing.Sha256;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Memory;
using System.Text.Json;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class RecurringExecutionRecoveryTests
{
    private static EnqueueRequest Request => new("scan", "{}", new JobPolicy(), TimeSpan.Zero);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StaleExecutionDoesNotBlockRecurringWork(bool pruneExecution, CancellationToken ct)
    {
        var clock = new DebounceClock();
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        await using var store = new RecurringRecoveryStore(database, clock);
        await store.AddRecurring("scan", Request, TimeSpan.FromHours(1), ct, coalesceOccurrences: true);
        string first = (await store.RunRecurring("scan", ct))!;
        string markerId = new Sha256HashingUtil().Hash(JsonSerializer.Serialize(first));
        var markers = await database.GetContainer("flywheel.scheduleExecutions", ct);
        string marker = (await markers.GetItem(markerId, ct))!;
        await store.Cancel(first, ct);
        await store.SetRecurringEnabled("scan", false, cancellationToken: ct);
        if (pruneExecution)
        {
            await store.Maintain(100, ct);
            Check(await store.Get(first, ct) is null, "Completed execution was not pruned.");
        }
        Check(await database.Execute(new LibrarianBatch([
            new LibrarianWrite("flywheel.scheduleExecutions", markerId, marker)]), ct), "Could not restore stale marker.");

        // Automatic scheduling must heal the marker without a manual run or a restart.
        await store.SetRecurringEnabled("scan", true, cancellationToken: ct);
        clock.Advance(TimeSpan.FromHours(1));
        await store.Maintain(100, ct);
        var schedule = (await store.ListRecurring(cancellationToken: ct)).Single();
        Check(schedule.LastExecutionId != first && schedule.LastExecutionId is not null, "Stale marker blocked the next occurrence.");
        Check(schedule.CoalescedOccurrences == 0, "Stale work was counted as coalesced.");
        Check(await markers.GetItem(markerId, ct) is null, "Stale marker was not removed.");
        Check(await store.RunRecurring("scan", ct) == schedule.LastExecutionId, "Live replacement was not coalesced.");
    }

    [Test]
    public async Task RetentionRemovesOrphanedExecutionMarker(CancellationToken ct)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        await using var store = new RecurringRecoveryStore(database);
        await store.AddRecurring("scan", Request, TimeSpan.FromHours(1), ct, coalesceOccurrences: true);
        string first = (await store.RunRecurring("scan", ct))!;
        string markerId = new Sha256HashingUtil().Hash(JsonSerializer.Serialize(first));
        var markers = await database.GetContainer("flywheel.scheduleExecutions", ct);
        string marker = (await markers.GetItem(markerId, ct))!;
        await store.Cancel(first, ct);
        await store.SetRecurringEnabled("scan", false, cancellationToken: ct);
        Check(await database.Execute(new LibrarianBatch([
            new LibrarianWrite("flywheel.scheduleExecutions", markerId, marker)]), ct), "Could not restore stale marker.");

        await store.Maintain(100, ct);
        Check(await store.Get(first, ct) is null, "Completed execution was not pruned.");
        Check(await markers.GetItem(markerId, ct) is null, "Retention left an orphaned execution marker.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
