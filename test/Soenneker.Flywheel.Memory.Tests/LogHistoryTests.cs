using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Logging.Dtos;
using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class LogHistoryTests
{
    [Test]
    public async Task PagesRetainEveryEntryAcrossAttemptsAndExpire(CancellationToken cancellationToken)
    {
        var clock = new DebounceClock();
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        string id = await store.Enqueue(new EnqueueRequest("logs", "{}", new JobPolicy { MaxAttempts = 2 }, TimeSpan.Zero), cancellationToken);
        JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken))!;
        for (int batch = 0; batch < 65; batch++)
            await store.AppendLogs(lease, Enumerable.Range(batch * 50, 50)
                .Select(i => new JobLogMessage("Information", "test", i.ToString())).ToArray(), cancellationToken);
        await store.Finish(lease, JobOutcome.Failed, "retry", TimeSpan.Zero, cancellationToken);
        lease = (await store.Claim("worker", TimeSpan.FromMinutes(1), cancellationToken))!;
        await store.AppendLogs(lease, [new JobLogMessage("Information", "test", "retry entry")], cancellationToken);
        var all = new List<JobLogEntry>();
        string? cursor = null;
        while (true)
        {
            var page = await store.GetLogs(id, 137, cancellationToken, cursor);
            if (page.Count == 0) break;
            all.InsertRange(0, page);
            cursor = page[0].Id;
        }
        if (all.Count != 3251 || all[0].Message != "0" || all[^1].Attempt != 2 ||
            !all.Take(3250).Select(e => e.Message).SequenceEqual(Enumerable.Range(0, 3250).Select(i => i.ToString())))
            throw new Exception("Log paging lost, duplicated, or reordered retained entries.");
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken);
        clock.Advance(TimeSpan.FromDays(31));
        for (int i = 0; i < 3; i++) await store.Maintain(100, cancellationToken);
        if ((await store.GetLogs(id, cancellationToken: cancellationToken)).Count != 0)
            throw new Exception("Expired logs remained visible.");
    }
}
