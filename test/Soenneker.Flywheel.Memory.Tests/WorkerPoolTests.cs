using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Dashboard;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class WorkerPoolTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    [Test]
    public async Task PoolsRouteBeforeConsumingAttemptsOrLimits(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.ConfigureMethod("restricted", new MethodPolicy { Pools = ["X", "Y"], MaxConcurrency = 1, RateLimit = 1 }, cancellationToken);
        string restricted = await store.Enqueue(new EnqueueRequest("restricted", "{}", new JobPolicy(), TimeSpan.Zero), cancellationToken);
        string normal = await store.Enqueue(new EnqueueRequest("normal", "{}", new JobPolicy(), TimeSpan.Zero), cancellationToken);
        var names = new HashSet<string> { "restricted", "normal" };

        if (await store.ClaimForWorker("wrong", LeaseDuration, "v1", names, "x", cancellationToken) is not null)
            throw new Exception("Pool names must match exactly, and named pools cannot claim default work.");
        if ((await store.Get(restricted, cancellationToken))!.Attempt != 0)
            throw new Exception("An ineligible pool consumed an attempt.");
        JobLease first = (await store.ClaimForWorker("default", LeaseDuration, "v1", names, cancellationToken))!;
        if (first.Job.Id != normal) throw new Exception("Default workers claimed restricted work or failed to skip it.");
        JobLease second = (await store.ClaimForWorker("allowed", LeaseDuration, "v1", names, "Y", cancellationToken))!;
        if (second.Job.Id != restricted || second.Job.Attempt != 1 || second.Job.WorkerPool != "Y")
            throw new Exception("An allowed pool could not claim its job after ineligible probes.");
    }

    [Test]
    public async Task SharedPoolLimitsSurviveEquivalentRegistration(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.ConfigureMethod("job", new MethodPolicy { Pools = ["X", "Y"], RateLimit = 1, RateWindow = TimeSpan.FromHours(1) }, cancellationToken);
        await store.Enqueue(new EnqueueRequest("job", "{}", new JobPolicy(), TimeSpan.Zero), cancellationToken);
        await store.Enqueue(new EnqueueRequest("job", "{}", new JobPolicy(), TimeSpan.Zero), cancellationToken);
        var names = new HashSet<string> { "job" };
        JobLease lease = (await store.ClaimForWorker("x", LeaseDuration, "v1", names, "X", cancellationToken))!;
        await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken);
        await store.ConfigureMethod("job", new MethodPolicy { Pools = ["Y", "X"], RateLimit = 1, RateWindow = TimeSpan.FromHours(1) }, cancellationToken);
        if (await store.ClaimForWorker("y", LeaseDuration, "v1", names, "Y", cancellationToken) is not null)
            throw new Exception("Equivalent pool registration reset the shared rate limit.");
    }

    [Test]
    public async Task ServerSnapshotsRetainPoolAndDefaultCompatibility(CancellationToken cancellationToken)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await store.Heartbeat("named", [new("X", 4), new("Y", 2)], LeaseDuration, cancellationToken);
        await store.Heartbeat("legacy", 2, LeaseDuration, cancellationToken);
        var servers = await store.ListServers(cancellationToken: cancellationToken);
        if (servers.Single(server => server.Id == "named").Pools.Single(pool => pool.Name == "X").Workers != 4 || servers.Single(server => server.Id == "legacy").Pools.Single().Name != "default")
            throw new Exception("Server list lost pool metadata.");
        var server = (await store.GetServer("named", cancellationToken))!;
        if (new DashboardSnapshotFactory().Server(server).Pools.Single(pool => pool.Name == "X").Workers != 4)
            throw new Exception("Dashboard projection lost the worker pool.");
        await store.Heartbeat("named", [new("Y", 4)], LeaseDuration, cancellationToken);
        if ((await store.GetServer("named", cancellationToken))!.Pools.Single().Name != "Y")
            throw new Exception("Server pool changes were not persisted.");
    }

    [Test]
    public void NamedPoolsMustLeaveWorkersForDefault()
    {
        new FlywheelOptions { Workers = 8, Pools = new() { ["X"] = 3, ["Y"] = 2 } }.Validate();
        foreach (FlywheelOptions options in new FlywheelOptions[]
        {
            new() { Workers = 2, Pools = new() { ["X"] = 1, ["Y"] = 1 } },
            new() { Workers = 4, Pools = new() { ["X"] = 4 } },
            new() { Workers = 4, Pools = new() { ["X"] = 0 } },
            new() { Workers = 4, Pools = new() { ["default"] = 1 } },
            new() { Workers = 1, Pools = new() { ["X"] = 1 } }
        })
        {
            try { options.Validate(); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new Exception("Invalid pool allocation was accepted.");
        }
    }

    [Test]
    public void RejectsInvalidPoolConfiguration()
    {
        foreach (string pool in new[] { "", " ", new string('x', 201) })
        {
            try { new FlywheelOptions { Pools = new() { [pool] = 1 } }.Validate(); }
            catch (ArgumentOutOfRangeException)
            {
                try { new MethodPolicy { Pools = [pool] }.Validate(); }
                catch (ArgumentOutOfRangeException) { continue; }
            }
            throw new Exception("Invalid pool configuration was accepted.");
        }
    }
}
