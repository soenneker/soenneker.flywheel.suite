using System.Diagnostics;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Memory;

internal static class MemoryInteropBenchmark
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance() => _now += TimeSpan.FromMilliseconds(1);
    }

    internal static async Task Run()
    {
        var clock = new Clock();
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions(), clock);
        string payload = "{\"value\":\"" + new string('x', 4096) + "\"}";
        EnqueueRequest Request() => new("work", payload, new JobPolicy(), TimeSpan.Zero);
        for (int i = 0; i < 1000; i++) await store.Enqueue(Request());
        JobLease lease = (await store.Claim("worker", TimeSpan.FromMinutes(10)))!;
        Console.WriteLine("Operation,MedianMicroseconds,BytesPerOperation");
        await Measure("Renew-1000-jobs", async () =>
        {
            clock.Advance();
            if (await store.Renew(lease, TimeSpan.FromMinutes(10)) != LeaseStatus.Renewed) throw new Exception("Lease lost.");
        });
        await Measure("Sample-1000-jobs", async () => { clock.Advance(); await store.SampleLiveActivity(default); });
        for (int i = 0; i < 50; i++)
        {
            await store.AddRecurring("schedule-" + i, Request(), TimeSpan.FromHours(1));
            await store.RunRecurring("schedule-" + i);
        }
        await Measure("ListRecurring-50", async () =>
        {
            if ((await store.ListRecurring()).Count != 50) throw new Exception("Missing schedules.");
        });
    }

    private static async Task Measure(string name, Func<Task> action)
    {
        for (int i = 0; i < 10; i++) await action();
        const int iterations = 100;
        var elapsed = new double[5];
        var allocated = new long[5];
        for (int round = 0; round < 5; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long bytes = GC.GetTotalAllocatedBytes(true);
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) await action();
            elapsed[round] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / iterations;
            allocated[round] = (GC.GetTotalAllocatedBytes(true) - bytes) / iterations;
        }
        Array.Sort(elapsed);
        Array.Sort(allocated);
        Console.WriteLine($"{name},{elapsed[2]:F3},{allocated[2]}");
    }
}
