using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task SearchHistoryMatchesSearchAcrossPagesStatesAndDateBounds() => WithStore(async (store, db, ns) =>
    {
        string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ns)));
        string prefix = $"flywheel:{{{tag}}}:v1:";
        long timestamp = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        JobState[] states = [JobState.Scheduled, JobState.Running, JobState.Succeeded, JobState.DeadLettered, JobState.Cancelled, JobState.Waiting];
        for (int i = 0; i < 230; i++)
        {
            var job = new JobRecord { Id = $"job-{i:D3}", Name = i < 220 ? "matching" : "unrelated", Owner = "worker-a",
                Payload = "{}", Policy = new(), CreatedAt = timestamp, UpdatedAt = timestamp + i * 60000L, State = states[i % states.Length] };
            await db.HashSetAsync(prefix + "jobs", job.Id, JsonSerializer.Serialize(job));
            await db.SortedSetAddAsync(prefix + "all", job.Id, timestamp);
        }
        foreach (string query in new[] { "matching", "Queued", "Scheduled", "WAITING", "worker-a", "job-229", "absent" })
        {
            var search = await store.Search(query, 0, 10);
            var points = await store.GetSearchHistory(query, null, null);
            Check(points.Sum(p => p.Scheduled + p.Running + p.Succeeded + p.DeadLettered + p.Cancelled + p.Waiting + p.Queued) == search.TotalCount,
                "Chart count differs from all matching jobs");
        }
        DateTimeOffset start = DateTimeOffset.FromUnixTimeMilliseconds(timestamp + 600000);
        DateTimeOffset end = start.AddMinutes(11);
        var filtered = await store.Search("matching", start, end, 0, 10);
        var history = await store.GetSearchHistory("matching", start, end);
        Check(filtered.TotalCount == 11 && history.Sum(p => p.Scheduled + p.Running + p.Succeeded + p.DeadLettered + p.Cancelled + p.Waiting + p.Queued) == 11,
            "Date boundaries do not match the table");
        Check((await store.GetSearchHistory("matching", null, null)).Sum(p => p.Waiting) > 0, "Waiting jobs were omitted");
    });
}
