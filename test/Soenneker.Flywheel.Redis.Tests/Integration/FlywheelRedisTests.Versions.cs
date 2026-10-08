using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Generated;
using System.Threading;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public ValueTask VersionSubmissionIsAtomicAndSurvivesRetention(CancellationToken cancellationToken) => new ValueTask(WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns, retainCompletedJobs: false);
        string[] ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            (i % 2 == 0 ? store : other).EnqueueForCurrentInstance(Request(), "build-2", "new", cancellationToken: cancellationToken)));
        Check(ids.Distinct().Count() == 1 && (await store.List(cancellationToken: cancellationToken)).Count == 1, "Concurrent submissions duplicated a version job");
        JobLease lease = (await other.ClaimForVersion("new", TimeSpan.FromSeconds(30), "build-2", cancellationToken: cancellationToken))!;
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Completion failed");
        await other.Maintain(100, cancellationToken: cancellationToken);
        Check(await store.Get(ids[0], cancellationToken: cancellationToken) is null, "Retention did not remove completed job");
        Check(await other.EnqueueForCurrentInstance(Request(), "build-2", "new", cancellationToken: cancellationToken) == ids[0], "Retention removed once-per-version marker");
        Check(await store.EnqueueForCurrentInstance(Request(), "build-3", "new", cancellationToken: cancellationToken) != ids[0], "Different builds shared a submission");
        Check(await store.EnqueueForCurrentInstance(Request() with { Name = "other" }, "build-2", "new", cancellationToken: cancellationToken) != ids[0], "Different jobs shared a submission");
    }));

    [Test]
    public ValueTask VersionDispatchSkipsIncompatibleJobsBeforeSelectingMethod(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        string restricted = await store.EnqueueForCurrentInstance(Request() with
        {
            Policy = new JobPolicy { Priority = JobPriority.Critical }
        }, "build-2", "new-0", cancellationToken: cancellationToken);
        string ordinary = await store.Enqueue(Request(), cancellationToken: cancellationToken);
        JobLease first = (await store.ClaimForVersion("old", TimeSpan.FromSeconds(30), "build-1", cancellationToken: cancellationToken))!;
        Check(first.Job.Id == ordinary, "Incompatible high priority job blocked ordinary work of the same method");
        Check(await store.Claim("unversioned", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null, "Unversioned claim took restricted work");
        Check(await store.ClaimForVersion("old", TimeSpan.FromSeconds(30), "BUILD-2", cancellationToken: cancellationToken) is null, "Version matching was not exact");
        Check((await store.Get(restricted, cancellationToken: cancellationToken))!.Attempt == 0, "Incompatible claims consumed attempts");
        JobLease?[] claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            store.ClaimForVersion("new-" + i, TimeSpan.FromSeconds(30), "build-2", cancellationToken: cancellationToken)));
        Check(claims.Count(x => x is not null) == 1, "Matching runners did not obtain exactly one lease");
        Check(claims.Single(x => x is not null)!.Job.Id == restricted, "Matching runner claimed the wrong job");
    }));

    [Test]
    public ValueTask VersionRestrictionSurvivesRetriesAndRecovery(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        string id = await store.EnqueueForCurrentInstance(Request(), "build-2", "new", cancellationToken: cancellationToken);
        JobLease first = (await store.ClaimForVersion("new", TimeSpan.FromSeconds(30), "build-2", cancellationToken: cancellationToken))!;
        Check(await store.Finish(first, JobOutcome.Failed, "retry", TimeSpan.Zero, cancellationToken: cancellationToken), "Retry failed");
        Check(await store.ClaimForVersion("old", TimeSpan.FromSeconds(30), "build-1", cancellationToken: cancellationToken) is null, "Old runner claimed retry");
        JobLease second = (await store.ClaimForVersion("new", TimeSpan.FromMilliseconds(100), "build-2", cancellationToken: cancellationToken))!;
        await Task.Delay(200, cancellationToken: cancellationToken);
        await store.Maintain(100, cancellationToken: cancellationToken);
        await Task.Delay(50, cancellationToken: cancellationToken);
        Check(await store.Claim("unversioned", TimeSpan.FromSeconds(30), cancellationToken: cancellationToken) is null, "Recovery removed version restriction");
        Check(await store.ClaimForVersion("old", TimeSpan.FromSeconds(30), "build-1", cancellationToken: cancellationToken) is null, "Old runner claimed recovered job");
        JobLease third = (await store.ClaimForVersion("new", TimeSpan.FromSeconds(30), "build-2", cancellationToken: cancellationToken))!;
        Check(third.Job.Id == id && third.Job.Attempt == 3 && third.Job.ApplicationVersion == "build-2",
            "Retry or recovery lost job identity or version");
        Check(!await store.Finish(second, JobOutcome.Succeeded, null, TimeSpan.Zero, cancellationToken: cancellationToken), "Recovered lease could still commit");
    }));

    [Test]
    public ValueTask VersionClientAndExecutorUseHostingApplicationVersion(CancellationToken cancellationToken) => new ValueTask(WithStore(async store =>
    {
        ServiceProvider Host(string version)
        {
            var services = new ServiceCollection();
        services.AddSingleton<System.Text.Json.Serialization.JsonSerializerContext>(TestJsonContext.Default);
            services.AddLogging();
            services.AddFlywheel(o => o.ApplicationVersion = version).AddGeneratedJobs();
            services.AddSingleton<IJobStore>(store);
            services.AddSingleton<InvocationState>();
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }
        await using ServiceProvider old = Host("build-1");
        await using ServiceProvider current = Host("build-2");
        await using ServiceProvider peer = Host("build-2");
        string id = await current.GetRequiredService<IJobClient>()
            .EnqueueForCurrentInstance(FlywheelJobs.IntegrationJobs_Run, new TestPayload("first"), cancellationToken: cancellationToken);
        string duplicate = await peer.GetRequiredService<IJobClient>()
            .EnqueueForCurrentInstance(FlywheelJobs.IntegrationJobs_Run, new TestPayload("second"), cancellationToken: cancellationToken);
        Check(id != duplicate, "Distinct instances shared a startup job");
        Check(await current.GetRequiredService<IJobClient>().EnqueueForCurrentInstance(FlywheelJobs.IntegrationJobs_Run, new TestPayload("ignored"), cancellationToken: cancellationToken) == id, "Same instance duplicated job");
        Check(!await old.GetRequiredService<IJobExecutor>().RunOnce(cancellationToken), "Old host executed new build's job");
        Check(await peer.GetRequiredService<IJobExecutor>().RunOnce(cancellationToken), "Matching peer did not execute job");
        Check(peer.GetRequiredService<InvocationState>().Value == "second", "Peer ran another instance's payload");
        Check(await current.GetRequiredService<IJobExecutor>().RunOnce(cancellationToken), "Original instance did not execute its job");
        Check(current.GetRequiredService<InvocationState>().Value == "first", "First payload did not win");
        Check(!await current.GetRequiredService<IJobExecutor>().RunOnce(cancellationToken), "Completed job ran twice");
        Check((await store.Get(id, cancellationToken: cancellationToken))!.State == JobState.Succeeded, "Matching execution did not complete");
    }));
}
