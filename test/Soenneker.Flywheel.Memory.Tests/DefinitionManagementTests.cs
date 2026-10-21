using Soenneker.Flywheel.Communication.Requests;
namespace Soenneker.Flywheel.Memory.Tests;

public sealed class DefinitionManagementTests
{
    [Test]
    public async Task PauseEditResumeAndDeletePreserveExecutionsAndRejectStaleEdits(CancellationToken ct)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new(), clock);
        await store.AddRecurring("schedule", new("job", "{}", new(), TimeSpan.Zero), TimeSpan.FromSeconds(5), ct);
        string old = (await store.RunRecurring("schedule", ct))!;
        if (!await store.SetRecurringEnabled("schedule", false, cancellationToken: ct)) throw new Exception("Pause failed.");
        var view = (await store.ListRecurring(cancellationToken: ct)).Single();
        if (view.Enabled || view.DueAt is not null) throw new Exception("Paused definition disappeared or remains due.");
        clock.Advance(TimeSpan.FromSeconds(20));
        await store.Maintain(100, ct);
        if ((await store.List(cancellationToken: ct)).Count != 1) throw new Exception("Paused schedule launched.");
        var definition = (await store.GetRecurringDefinition("schedule", ct))!.Definition;
        var update = definition with { Payload = "{\"new\":true}", IntervalMilliseconds = 10000 };
        if (!await store.UpdateRecurring("schedule", update, ct) || await store.UpdateRecurring("schedule", update, ct)) throw new Exception("Revision conflict not enforced.");
        if (!await store.SetRecurringEnabled("schedule", true, cancellationToken: ct)) throw new Exception("Resume failed.");
        await store.Maintain(100, ct);
        if ((await store.List(cancellationToken: ct)).Count != 1) throw new Exception("Resume caught up old runs.");
        clock.Advance(TimeSpan.FromSeconds(10));
        await store.Maintain(100, ct);
        if ((await store.List(cancellationToken: ct)).Count != 2 || (await store.Get(old, ct))!.Payload != "{}") throw new Exception("Updated schedule changed old execution.");
        await store.DeleteRecurring("schedule", cancellationToken: ct);
        if ((await store.ListRecurring(cancellationToken: ct)).Count != 0 || await store.Get(old, ct) is null) throw new Exception("Delete affected history.");
    }

    [Test]
    public async Task CronAndManualDefinitionsCanBeUpdated(CancellationToken ct)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new(), clock);
        var request = new EnqueueRequest("job", "{}", new(), TimeSpan.Zero);
        await store.AddCron("cron", request, "0 * * * *", cancellationToken: ct);
        var before = (await store.GetRecurringDefinition("cron", ct))!.Definition;
        await store.UpdateRecurring("cron", before with { Cron = "*/5 * * * *" }, ct);
        if ((await store.ListRecurring(cancellationToken: ct)).Single().DueAt != clock.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds()) throw new Exception("Cron was not recalculated.");
        await store.AddManual("manual", request, ct);
        string old = (await store.RunManual("manual", ct))!;
        await store.UpdateManual("manual", request with { Payload = "123" }, ct);
        string updated = (await store.RunManual("manual", ct))!;
        if ((await store.Get(updated, ct))!.Payload != "123" || (await store.Get(old, ct))!.Payload != "{}") throw new Exception("Manual update incorrect.");
        await store.DeleteManual("manual", ct);
        if (await store.RunManual("manual", ct) is not null || await store.Get(updated, ct) is null) throw new Exception("Manual deletion incorrect.");
    }
}
