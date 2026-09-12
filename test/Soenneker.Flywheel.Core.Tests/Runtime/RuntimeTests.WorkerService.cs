using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Services;
using Soenneker.Flywheel.Core.Services.Abstract;

namespace Soenneker.Flywheel.Core.Tests.Runtime;

public sealed partial class RuntimeTests
{
    [Test]
    public async Task IdleWorkersWakeFromNotificationsAndResize()
    {
        var executor = new EmptyExecutor();
        var store = new StubStore();
        var options = new FlywheelOptions { Workers = 256, PollInterval = TimeSpan.FromSeconds(5) };
        using var service = new WorkerService(executor, store, options, NullLogger<WorkerService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(deadline.Token);
        await WaitFor(() => executor.Claims >= 1, deadline.Token);
        int idleClaims = executor.Claims;
        if (idleClaims != 1) throw new Exception("Startup probes scaled with worker count");
        await Task.Delay(100, deadline.Token);
        if (executor.Claims != idleClaims) throw new Exception("Idle workers continued polling without a wake-up");

        store.Notify();
        await WaitFor(() => executor.Claims > idleClaims, deadline.Token);
        await Task.Delay(100, deadline.Token);
        if (executor.Claims != idleClaims + 1) throw new Exception("Notification woke more than one empty worker");

        await service.SetWorkerCount(1, deadline.Token);
        await Task.Delay(100, deadline.Token);
        int resizedClaims = executor.Claims;
        store.Notify();
        await WaitFor(() => executor.Claims > resizedClaims, deadline.Token);
        await Task.Delay(100, deadline.Token);
        if (executor.Claims != resizedClaims + 1) throw new Exception("Retired workers claimed more jobs");

        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task OneSignalFillsPoolBeforeHandlersFinishAndRetirementDoesNotCancelThem()
    {
        var executor = new BlockingExecutor(3);
        var store = new StubStore();
        var options = new FlywheelOptions { Workers = 3, PollInterval = TimeSpan.FromMinutes(1) };
        using var service = new WorkerService(executor, store, options, NullLogger<WorkerService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await service.StartAsync(default);
            await WaitFor(() => executor.Started == 3, deadline.Token);
            await service.SetWorkerCount(0, deadline.Token);
            await Task.Delay(50, deadline.Token);
            if (executor.Completed != 0) throw new Exception("Retirement interrupted a running handler");
            executor.Release.TrySetResult();
            await WaitFor(() => executor.Completed == 3, deadline.Token);
            int probes = executor.Probes;
            store.Notify();
            await Task.Delay(100, deadline.Token);
            if (executor.Probes != probes) throw new Exception("Paused pool claimed work");
            await service.SetWorkerCount(2, deadline.Token);
            await WaitFor(() => executor.Probes > probes, deadline.Token);
        }
        finally { await service.StopAsync(default); }
    }

    [Test]
    public async Task NotificationDuringEmptyClaimIsNotLost()
    {
        var executor = new BlockingExecutor(0) { BlockEmpty = true };
        var store = new StubStore();
        using var service = new WorkerService(executor, store,
            new FlywheelOptions { Workers = 1, PollInterval = TimeSpan.FromMinutes(1) }, NullLogger<WorkerService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await service.StartAsync(default);
            await WaitFor(() => executor.Probes == 1, deadline.Token);
            store.Notify();
            executor.Release.TrySetResult();
            await WaitFor(() => executor.Probes == 2, deadline.Token);
        }
        finally { await service.StopAsync(default); }
    }

    [Test]
    public async Task RecoveryProbesOnceRegardlessOfWorkerCount()
    {
        var executor = new EmptyExecutor();
        using var service = new WorkerService(executor, new StubStore(),
            new FlywheelOptions { Workers = 64, PollInterval = TimeSpan.FromMilliseconds(100) }, NullLogger<WorkerService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await service.StartAsync(default);
            await WaitFor(() => executor.Claims >= 3, deadline.Token);
            if (executor.Claims > 5) throw new Exception("Recovery broadcast to the pool");
        }
        finally { await service.StopAsync(default); }
    }

    private sealed class BlockingExecutor(int jobs) : IJobExecutor
    {
        private int _probes, _started, _completed;
        public int Probes => Volatile.Read(ref _probes);
        public int Started => Volatile.Read(ref _started);
        public int Completed => Volatile.Read(ref _completed);
        public bool BlockEmpty;
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> RunOnce(CancellationToken cancellationToken) => RunOnce(static () => { }, cancellationToken);

        public async Task<bool> RunOnce(Action onClaimed, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _probes) > jobs)
            {
                if (BlockEmpty) await Release.Task.WaitAsync(cancellationToken);
                return false;
            }
            Interlocked.Increment(ref _started);
            onClaimed();
            await Release.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref _completed);
            return true;
        }
    }

    private static async Task WaitFor(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(10, token);
    }

}
