using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task SearchHistoryMatchesSearchAcrossPagesStatesAndDateBounds() => WithStore(async (store, db, ns) =>
    {
        long timestamp = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        JobState[] states = [JobState.Scheduled, JobState.Running, JobState.Succeeded, JobState.DeadLettered, JobState.Cancelled, JobState.Waiting];
        for (int i = 0; i < 230; i++)
        {
            var job = new JobRecord { Id = $"job-{i:D3}", Name = i < 220 ? "matching" : "unrelated", Owner = "worker-a",
                Payload = "{}", Policy = new JobPolicy(), CreatedAt = timestamp, UpdatedAt = timestamp + i * 60000L, State = states[i % states.Length] };
            await SeedJob(db, ns, job);
        }
        foreach (string query in new[] { "matching", "Queued", "Scheduled", "WAITING", "worker-a", "job-229", "absent" })
        {
            JobSearchResult search = await store.Search(query, 0, 10);
            IReadOnlyList<JobHistoryPoint> points = await store.GetSearchHistory(query, null, null);
            Check(points.Sum(p => p.Scheduled + p.Running + p.Succeeded + p.DeadLettered + p.Cancelled + p.Waiting + p.Queued) == search.TotalCount,
                "Chart count differs from all matching jobs");
        }
        DateTimeOffset start = DateTimeOffset.FromUnixTimeMilliseconds(timestamp + 600000);
        DateTimeOffset end = start.AddMinutes(11);
        JobSearchResult filtered = await store.Search("matching", start, end, 0, 10);
        IReadOnlyList<JobHistoryPoint> history = await store.GetSearchHistory("matching", start, end);
        Check(filtered.TotalCount == 11 && history.Sum(p => p.Scheduled + p.Running + p.Succeeded + p.DeadLettered + p.Cancelled + p.Waiting + p.Queued) == 11,
            "Date boundaries do not match the table");
        Check((await store.GetSearchHistory("matching", null, null)).Sum(p => p.Waiting) > 0, "Waiting jobs were omitted");
    });
}
