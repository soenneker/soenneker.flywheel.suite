using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Services;

namespace Soenneker.Flywheel.Core.Tests.Runtime;

public sealed partial class RuntimeTests
{
    [Test]
    public async Task NamedPoolsHaveIndependentCapacityAndResize(CancellationToken cancellationToken)
    {
        var executor = new PoolTestExecutor(true);
        var options = new FlywheelOptions
        {
            Workers = 5, Pools = new() { ["X"] = 2, ["Y"] = 1 },
            PollInterval = TimeSpan.FromMinutes(1), ShutdownGracePeriod = TimeSpan.FromMilliseconds(50)
        };
        options.Validate();
        using var service = new WorkerService(executor, new StubStore(), options, NullLogger<WorkerService>.Instance);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await service.StartAsync(deadline.Token);
            await WaitFor(() => executor.Calls.GetValueOrDefault("X") == 2 && executor.Calls.GetValueOrDefault("Y") == 1 && executor.Calls.GetValueOrDefault("default") == 2, deadline.Token);
            bool overBudgetRejected = false;
            try { await service.SetPoolWorkerCount("X", 3, deadline.Token); }
            catch (ArgumentOutOfRangeException) { overBudgetRejected = true; }
            if (!overBudgetRejected) throw new Exception("Pool resizing exceeded the configured worker budget.");
            await service.SetPoolWorkerCount("default", 1, deadline.Token);
            await service.SetPoolWorkerCount("X", 3, deadline.Token);
            await Task.Delay(100, deadline.Token);
            if (executor.Calls["X"] != 2) throw new Exception("Retiring workers did not retain their execution slots.");
            if (executor.Calls["Y"] != 1 || service.WorkerCount != 5) throw new Exception("Resizing X changed Y's capacity.");
            await service.SetPoolWorkerCount("X", 0, deadline.Token);
            await service.SetPoolWorkerCount("Y", 2, deadline.Token);
            await Task.Delay(100, deadline.Token);
            if (executor.Calls["Y"] != 1) throw new Exception("Retiring workers exceeded the host budget.");
            if (service.Pools.Single(pool => pool.Name == "X").Workers != 0 || service.WorkerCount != 3 || executor.Calls["X"] != 2)
                throw new Exception("Pausing X affected another pool or admitted more jobs.");
            if (!executor.Calls.ContainsKey("default")) throw new Exception("The default pool was not started.");
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Test]
    public async Task NotificationsWakeEachPoolOnce(CancellationToken cancellationToken)
    {
        var executor = new PoolTestExecutor(false);
        var store = new StubStore();
        using var service = new WorkerService(executor, store,
            new FlywheelOptions { Workers = 17, Pools = new() { ["X"] = 8, ["Y"] = 8 }, PollInterval = TimeSpan.FromMinutes(1) },
            NullLogger<WorkerService>.Instance);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await service.StartAsync(deadline.Token);
            await WaitFor(() => executor.Calls.GetValueOrDefault("X") == 1 && executor.Calls.GetValueOrDefault("Y") == 1, deadline.Token);
            store.Notify();
            await WaitFor(() => executor.Calls["X"] == 2 && executor.Calls["Y"] == 2, deadline.Token);
            await Task.Delay(100, deadline.Token);
            if (executor.Calls["X"] != 2 || executor.Calls["Y"] != 2) throw new Exception("Idle pool probes scaled with worker count.");
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }
}
