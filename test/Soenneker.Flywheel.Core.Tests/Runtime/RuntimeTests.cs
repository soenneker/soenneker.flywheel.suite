using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Services;

namespace Soenneker.Flywheel.Core.Tests.Runtime;

public sealed partial class RuntimeTests
{
    [Test]
    public async Task WorkerShutdownIsNotReportedAsJobTimeout()
    {
        var store = new StubStore { Status = Communication.Enums.LeaseStatus.Renewed };
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var executor = new JobExecutor(store, services.GetRequiredService<IServiceScopeFactory>(), [new WaitingInvoker()],
            new FlywheelOptions(), NullLogger<JobExecutor>.Instance);
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await executor.RunOnce(stopping.Token);
        if (store.Error != "Execution interrupted by worker shutdown")
            throw new Exception("Worker shutdown was misreported as a job timeout");
    }

    [Test]
    public async Task DependencyCancellationIsNotReportedAsJobTimeout()
    {
        var store = new StubStore { Status = Communication.Enums.LeaseStatus.Renewed };
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var executor = new JobExecutor(store, services.GetRequiredService<IServiceScopeFactory>(), [new CancelledDependencyInvoker()],
            new FlywheelOptions(), NullLogger<JobExecutor>.Instance);
        await executor.RunOnce(CancellationToken.None);
        if (store.Error != "A handler or dependency cancelled an operation before the job timeout")
            throw new Exception("A dependency cancellation was misreported as an execution timeout");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LeaseLossCancelsHandlerAndPreventsCommit(bool stalledRenewal)
    {
        var store = new StubStore { Status = Communication.Enums.LeaseStatus.Lost, StalledRenewal = stalledRenewal };
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var invoker = new WaitingInvoker();
        var executor = new JobExecutor(store, services.GetRequiredService<IServiceScopeFactory>(), [invoker],
            new FlywheelOptions { LeaseDuration = TimeSpan.FromMilliseconds(90) }, NullLogger<JobExecutor>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await executor.RunOnce(deadline.Token);
        if (!invoker.Cancelled || store.Commits != 0) throw new Exception("Lease loss did not prevent commit");
    }

    [Test]
    public async Task TimeoutAndDurableCancellationAreDistinct()
    {
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        foreach (LeaseStatus status in new[] { Communication.Enums.LeaseStatus.Renewed, Communication.Enums.LeaseStatus.CancellationRequested })
        {
            var store = new StubStore { Status = status };
            var executor = new JobExecutor(store, services.GetRequiredService<IServiceScopeFactory>(), [new WaitingInvoker()],
                new FlywheelOptions { LeaseDuration = TimeSpan.FromMilliseconds(90) }, NullLogger<JobExecutor>.Instance);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await executor.RunOnce(deadline.Token);
            JobOutcome expected = status == Communication.Enums.LeaseStatus.Renewed ? Communication.Enums.JobOutcome.Failed : Communication.Enums.JobOutcome.Cancelled;
            string expectedError = status == Communication.Enums.LeaseStatus.Renewed
                ? "Execution exceeded its configured timeout" : "Cancellation requested";
            if (store.Error?.StartsWith(expectedError, StringComparison.Ordinal) != true) throw new Exception("Cancellation reason was misreported");
            if (store.Commits != 1 || store.Outcome != expected) throw new Exception("Incorrect cancellation outcome");
        }
    }
}
