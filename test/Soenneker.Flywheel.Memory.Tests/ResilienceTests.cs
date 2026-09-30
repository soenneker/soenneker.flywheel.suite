using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Services;
using Soenneker.Flywheel.Core.Services.Abstract;

namespace Soenneker.Flywheel.Memory.Tests;

public sealed class ResilienceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static EnqueueRequest Request(string name = "test", JobPolicy? policy = null) =>
        new(name, "{}", policy ?? new JobPolicy(), TimeSpan.Zero);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    [Test]
    public async ValueTask ClaimsSkipUnsupportedHandlersWithoutConsumingAttempts()
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        string unknown = await store.Enqueue(Request("new-handler"));
        string known = await store.Enqueue(Request());
        JobLease lease = (await store.ClaimForWorker("old", TimeSpan.FromMinutes(1), "v1", new HashSet<string> { "test" }))!;
        Check(lease.Job.Id == known, "Unknown handler prevented eligible work from being claimed.");
        JobRecord untouched = (await store.Get(unknown))!;
        Check(untouched.State == JobState.Scheduled && untouched.Attempt == 0, "Unsupported work was consumed.");
        Check(await store.ClaimForWorker("old", TimeSpan.FromMinutes(1), "v1", new HashSet<string> { "test" }) is null,
            "Unsupported work was claimed after eligible work ran out.");
    }

    [Test]
    public async ValueTask InterruptedLeasePreservesRetryBudgetAndFencesTheOldOwner()
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        string id = await store.Enqueue(Request()); // Default MaxAttempts = 1.
        JobLease first = (await store.Claim("first", TimeSpan.FromMinutes(1)))!;
        Check(await store.Interrupt(first, "shutdown"), "Shutdown recovery failed.");
        JobRecord scheduled = (await store.Get(id))!;
        Check(scheduled.State == JobState.Scheduled && scheduled.Attempt == 0, "Shutdown exhausted the retry budget.");
        JobLease second = (await store.Claim("second", TimeSpan.FromMinutes(1)))!;
        Check(!await store.Interrupt(first, "stale") && !await store.Quarantine(first, "stale"), "Old owner changed a successor lease.");
        Check(await store.Finish(second, JobOutcome.Succeeded, null, TimeSpan.Zero), "Successor could not finish.");
    }

    [Test]
    public async ValueTask DurableCancellationWinsOverShutdownRecovery()
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        string id = await store.Enqueue(Request());
        JobLease lease = (await store.Claim("first", TimeSpan.FromMinutes(1)))!;
        await store.Cancel(id);
        await store.Interrupt(lease, "shutdown");
        Check((await store.Get(id))!.State == JobState.Cancelled && await store.Claim("second", TimeSpan.FromMinutes(1)) is null,
            "Shutdown resurrected a cancelled job.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async ValueTask ShutdownDrainsThenRecoversUnfinishedWork(bool finishDuringDrain)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new FlywheelOptions { Workers = 1, ShutdownGracePeriod = TimeSpan.FromMilliseconds(200) };
        var invoker = new Invoker(async (_, ct) => { started.SetResult(); await release.Task.WaitAsync(ct); });
        var executor = new JobExecutor(store, services.GetRequiredService<IServiceScopeFactory>(), [invoker], options, NullLogger<JobExecutor>.Instance);
        using var worker = new WorkerService(executor, store, options, NullLogger<WorkerService>.Instance);
        string id = await store.Enqueue(Request());
        await worker.StartAsync(default);
        await started.Task.WaitAsync(Limit);
        Task stopping = worker.StopAsync(default);
        if (finishDuringDrain) release.SetResult();
        await stopping.WaitAsync(Limit);
        JobRecord job = (await store.Get(id))!;
        Check(job.State == (finishDuringDrain ? JobState.Succeeded : JobState.Scheduled), "Incorrect shutdown outcome.");
        if (!finishDuringDrain) Check(job.Attempt == 0, "Shutdown consumed an attempt.");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async ValueTask IgnoredCancellationQuarantinesWithoutRetryAndKeepsScopeAlive(bool throwingCallback, bool policyTimeout)
    {
        await using var store = new MemoryJobStore(new FlywheelMemoryOptions());
        var resource = new ScopedResource();
        await using ServiceProvider services = new ServiceCollection().AddScoped(_ => resource).BuildServiceProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var lifetime = new Lifetime();
        var invoker = new Invoker(async (sp, ct) =>
        {
            _ = sp.GetRequiredService<ScopedResource>();
            using var registration = throwingCallback ? ct.Register(() => throw new InvalidOperationException("callback failure")) : default;
            started.SetResult();
            await release.Task;
        });
        var executor = new JobExecutor(store, services.GetRequiredService<IServiceScopeFactory>(), [invoker],
            new FlywheelOptions { CancellationGracePeriod = TimeSpan.FromMilliseconds(50) }, NullLogger<JobExecutor>.Instance, lifetime: lifetime);
        string id = await store.Enqueue(Request(policy: new JobPolicy { MaxAttempts = 3,
            Timeout = policyTimeout ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromMinutes(1) }));
        Task running = executor.RunOnce(stop.Token);
        try
        {
            await started.Task.WaitAsync(Limit);
            if (!policyTimeout) await stop.CancelAsync();
            try { await running.WaitAsync(Limit); throw new Exception("Unresponsive execution was accepted."); }
            catch (JobExecutionUnresponsiveException) { }
            Check(lifetime.Stopped && !resource.Disposed.Task.IsCompleted, "Host was not stopped or active handler scope was disposed.");
            Check((await store.Get(id))!.State == JobState.DeadLettered, "Unresponsive execution was not quarantined.");
            await store.Maintain(100);
            Check(await store.Claim("other", TimeSpan.FromMinutes(1)) is null, "Quarantine allowed an automatic duplicate.");
            try { await executor.RunOnce(default); throw new Exception("Unhealthy executor resumed claiming."); }
            catch (JobExecutionUnresponsiveException) { }
        }
        finally
        {
            release.TrySetResult();
            await resource.Disposed.Task.WaitAsync(Limit);
        }
    }

    private sealed class Invoker(Func<IServiceProvider, CancellationToken, Task> invoke) : IJobInvoker
    {
        public string Name => "test";
        public Task Invoke(IServiceProvider services, string payload, CancellationToken cancellationToken) => invoke(services, cancellationToken);
    }
    private sealed class ScopedResource : IDisposable
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => Disposed.TrySetResult();
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public bool Stopped;
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => Stopped = true;
    }
}
