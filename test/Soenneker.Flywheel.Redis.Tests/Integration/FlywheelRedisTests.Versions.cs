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

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task VersionSubmissionIsAtomicAndSurvivesRetention() => WithStore(async (store, db, ns) =>
    {
        var other = new RedisJobStore(_ => Task.FromResult(db), ns, retainCompletedJobs: false);
        string[] ids = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            (i % 2 == 0 ? store : other).EnqueueForCurrentVersion(Request(), "build-2")));
        Check(ids.Distinct().Count() == 1 && (await store.List()).Count == 1, "Concurrent submissions duplicated a version job");
        JobLease lease = (await other.ClaimForVersion("new", TimeSpan.FromSeconds(30), "build-2"))!;
        Check(await store.Finish(lease, JobOutcome.Succeeded, null, TimeSpan.Zero), "Completion failed");
        await other.Maintain(100);
        Check(await store.Get(ids[0]) is null, "Retention did not remove completed job");
        Check(await other.EnqueueForCurrentVersion(Request(), "build-2") == ids[0], "Retention removed once-per-version marker");
        Check(await store.EnqueueForCurrentVersion(Request(), "build-3") != ids[0], "Different builds shared a submission");
        Check(await store.EnqueueForCurrentVersion(Request() with { Name = "other" }, "build-2") != ids[0], "Different jobs shared a submission");
    });

    [Test]
    public Task VersionDispatchSkipsIncompatibleJobsBeforeSelectingMethod() => WithStore(async store =>
    {
        string restricted = await store.EnqueueForCurrentVersion(Request() with
        {
            Policy = new JobPolicy { Priority = JobPriority.Critical }
        }, "build-2");
        string ordinary = await store.Enqueue(Request());
        JobLease first = (await store.ClaimForVersion("old", TimeSpan.FromSeconds(30), "build-1"))!;
        Check(first.Job.Id == ordinary, "Incompatible high priority job blocked ordinary work of the same method");
        Check(await store.Claim("unversioned", TimeSpan.FromSeconds(30)) is null, "Unversioned claim took restricted work");
        Check(await store.ClaimForVersion("old", TimeSpan.FromSeconds(30), "BUILD-2") is null, "Version matching was not exact");
        Check((await store.Get(restricted))!.Attempt == 0, "Incompatible claims consumed attempts");
        JobLease?[] claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            store.ClaimForVersion("new-" + i, TimeSpan.FromSeconds(30), "build-2")));
        Check(claims.Count(x => x is not null) == 1, "Matching runners did not obtain exactly one lease");
        Check(claims.Single(x => x is not null)!.Job.Id == restricted, "Matching runner claimed the wrong job");
    });

    [Test]
    public Task VersionRestrictionSurvivesRetriesAndRecovery() => WithStore(async store =>
    {
        string id = await store.EnqueueForCurrentVersion(Request(), "build-2");
        JobLease first = (await store.ClaimForVersion("new", TimeSpan.FromSeconds(30), "build-2"))!;
        Check(await store.Finish(first, JobOutcome.Failed, "retry", TimeSpan.Zero), "Retry failed");
        Check(await store.ClaimForVersion("old", TimeSpan.FromSeconds(30), "build-1") is null, "Old runner claimed retry");
        JobLease second = (await store.ClaimForVersion("new", TimeSpan.FromMilliseconds(100), "build-2"))!;
        await Task.Delay(200);
        await store.Maintain(100);
        await Task.Delay(50);
        Check(await store.Claim("unversioned", TimeSpan.FromSeconds(30)) is null, "Recovery removed version restriction");
        Check(await store.ClaimForVersion("old", TimeSpan.FromSeconds(30), "build-1") is null, "Old runner claimed recovered job");
        JobLease third = (await store.ClaimForVersion("new", TimeSpan.FromSeconds(30), "build-2"))!;
        Check(third.Job.Id == id && third.Job.Attempt == 3 && third.Job.ApplicationVersion == "build-2",
            "Retry or recovery lost job identity or version");
        Check(!await store.Finish(second, JobOutcome.Succeeded, null, TimeSpan.Zero), "Recovered lease could still commit");
    });

    [Test]
    public Task VersionClientAndExecutorUseHostingApplicationVersion() => WithStore(async store =>
    {
        ServiceProvider Host(string version)
        {
            var services = new ServiceCollection();
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
            .EnqueueForCurrentVersion(FlywheelJobs.IntegrationJobs_Run, new TestPayload("first"));
        string duplicate = await peer.GetRequiredService<IJobClient>()
            .EnqueueForCurrentVersion(FlywheelJobs.IntegrationJobs_Run, new TestPayload("second"));
        Check(id == duplicate, "Hosts of the same build submitted duplicate jobs");
        Check(!await old.GetRequiredService<IJobExecutor>().RunOnce(default), "Old host executed new build's job");
        Check(await peer.GetRequiredService<IJobExecutor>().RunOnce(default), "Matching peer did not execute job");
        Check(peer.GetRequiredService<InvocationState>().Value == "first", "First submission's payload did not win");
        Check(!await current.GetRequiredService<IJobExecutor>().RunOnce(default), "Second matching host ran completed job");
        Check((await store.Get(id))!.State == JobState.Succeeded, "Matching execution did not complete");
    });
}
