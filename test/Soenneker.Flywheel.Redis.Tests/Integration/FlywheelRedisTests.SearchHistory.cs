using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Responses;
using System.Threading;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask CompletingOlderJobMovesItAheadOfNewerJobs(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        string olderId = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease lease = (await store.Claim("worker", TimeSpan.FromSeconds(30), cancellationToken))!;
        await Task.Delay(20, cancellationToken);
        string newerId = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        Check((await store.Search(null, 0, 1, cancellationToken)).Items.Single().Id == newerId,
            "Older running job took precedence over a newer update");
        await Task.Delay(20, cancellationToken);
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken), "Completion failed");
        Check((await store.Search(null, 0, 1, cancellationToken)).Items.Single().Id == olderId,
            "Completion did not move the older job to the first search page");
        Check((await store.List(0, 1, cancellationToken)).Single().Id == olderId,
            "Completion did not update the persisted list index");
    }));

    [Test]
    public ValueTask SearchHistoryMatchesSearchAcrossPagesStatesAndDateBounds(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        long timestamp = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        JobState[] states = [JobState.Scheduled, JobState.Running, JobState.Succeeded, JobState.DeadLettered, JobState.Cancelled, JobState.Waiting];
        for (int i = 0; i < 230; i++)
        {
            var job = new JobRecord { Id = $"job-{i:D3}", Name = i < 220 ? "matching" : "unrelated", Owner = "worker-a",
                Payload = "{}", Policy = new JobPolicy(), CreatedAt = timestamp, UpdatedAt = timestamp + i * 60000L, State = states[i % states.Length] };
            await SeedJob(db, ns, job);
        }
        // Cross the 200-record cursor boundary and verify state never overrides update ordering.
        var orderedIds = new List<string>();
        for (int offset = 0; offset < 230; offset += 50)
        {
            JobSearchResult page = await store.Search(null, offset, 50, cancellationToken);
            Check(page.TotalCount == 230, "Search total changed across pages");
            orderedIds.AddRange(page.Items.Select(job => job.Id));
        }
        Check(orderedIds.SequenceEqual(Enumerable.Range(0, 230).Reverse().Select(i => $"job-{i:D3}")),
            "Search is not ordered by newest update across pages and states");
        Check((await store.List(0, 10, cancellationToken)).Select(job => job.Id).SequenceEqual(orderedIds.Take(10)),
            "List and search ordering differ");
        foreach (string query in new[] { "matching", "Queued", "Scheduled", "WAITING", "worker-a", "job-229", "absent" })
        {
            JobSearchResult search = await store.Search(query, 0, 10, cancellationToken: cancellationToken);
            IReadOnlyList<JobHistoryPoint> points = await store.GetSearchHistory(query, null, null, cancellationToken: cancellationToken);
            Check(points.Sum(p => p.Scheduled + p.Running + p.Succeeded + p.DeadLettered + p.Cancelled + p.Waiting + p.Queued) == search.TotalCount,
                "Chart count differs from all matching jobs");
        }
        DateTimeOffset start = DateTimeOffset.FromUnixTimeMilliseconds(timestamp + 600000);
        DateTimeOffset end = start.AddMinutes(11);
        JobSearchResult filtered = await store.Search("matching", start, end, 0, 10, cancellationToken: cancellationToken);
        IReadOnlyList<JobHistoryPoint> history = await store.GetSearchHistory("matching", start, end, cancellationToken: cancellationToken);
        Check(filtered.TotalCount == 11 && history.Sum(p => p.Scheduled + p.Running + p.Succeeded + p.DeadLettered + p.Cancelled + p.Waiting + p.Queued) == 11,
            "Date boundaries do not match the table");
        Check((await store.GetSearchHistory("matching", null, null, cancellationToken: cancellationToken)).Sum(p => p.Waiting) > 0, "Waiting jobs were omitted");
    }));
}
