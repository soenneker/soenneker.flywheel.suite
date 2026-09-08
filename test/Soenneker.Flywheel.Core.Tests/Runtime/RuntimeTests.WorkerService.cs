using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Services;

namespace Soenneker.Flywheel.Core.Tests.Runtime;

public sealed partial class RuntimeTests
{
    [Test]
    public async Task IdleWorkersWakeFromNotificationsAndResize()
    {
        var executor = new EmptyExecutor();
        var store = new StubStore();
        var options = new FlywheelOptions { Workers = 3, PollInterval = TimeSpan.FromSeconds(5) };
        var service = new WorkerService(executor, store, options, NullLogger<WorkerService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(deadline.Token);
        await WaitFor(() => executor.Claims >= 3, deadline.Token);
        int idleClaims = executor.Claims;
        await Task.Delay(100, deadline.Token);
        if (executor.Claims != idleClaims) throw new Exception("Idle workers continued polling without a wake-up");

        store.Notify();
        await WaitFor(() => executor.Claims >= idleClaims + 3, deadline.Token);

        await service.SetWorkerCount(1, deadline.Token);
        await Task.Delay(100, deadline.Token);
        int resizedClaims = executor.Claims;
        store.Notify();
        await WaitFor(() => executor.Claims > resizedClaims, deadline.Token);
        await Task.Delay(100, deadline.Token);
        if (executor.Claims != resizedClaims + 1) throw new Exception("Retired workers claimed more jobs");

        await service.StopAsync(CancellationToken.None);
    }

    private static async Task WaitFor(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }

}
