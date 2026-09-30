using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Registrars;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis.Tests;

public sealed partial class FlywheelRedisTests
{
    [Test]
    public Task ReleaseIsolationSeparatesJobsDeduplicationAndSchedulesFromOldWorkers() => WithStore(async (legacy, db, ns) =>
    {
        ServiceProvider Open(string version)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddFlywheel(o => { o.ApplicationVersion = version; o.IsolateApplicationVersion = true; })
                .AddRedis(o => { o.Namespace = ns; o.ConnectionString = Environment.GetEnvironmentVariable("FLYWHEEL_TEST_REDIS") ?? "localhost:6379"; });
            return services.BuildServiceProvider();
        }
        string[] releases = ["v1", "v2"];
        try
        {
            await using var oldServices = Open(releases[0]);
            await using var newServices = Open(releases[1]);
            await using var peerServices = Open(releases[1]);
            var oldStore = oldServices.GetRequiredService<RedisJobStore>();
            var newStore = newServices.GetRequiredService<RedisJobStore>();
            var peer = peerServices.GetRequiredService<RedisJobStore>();
            var request = Request() with { IdempotencyKey = "same-key" };
            string newer = await newStore.Enqueue(request);
            Check(await oldStore.Claim("old", TimeSpan.FromMinutes(1)) is null, "Old release consumed new work.");
            Check(await legacy.Claim("legacy", TimeSpan.FromMinutes(1)) is null, "Unmodified legacy worker consumed new work.");
            Check(await oldStore.Get(newer) is null && await legacy.Get(newer) is null, "Jobs leaked across releases.");
            Check(await peer.Enqueue(request) == newer, "Same-release deduplication was lost.");
            string older = await oldStore.Enqueue(request);
            Check(older != newer, "Deduplication collided across releases.");
            Check((await peer.Claim("peer", TimeSpan.FromMinutes(1)))!.Job.Id == newer, "Same-release peer cannot claim work.");
            Check(await oldStore.AddRecurring("same-schedule", Request(), TimeSpan.FromHours(1)), "Old schedule registration failed.");
            Check(await newStore.AddRecurring("same-schedule", Request(), TimeSpan.FromHours(1)), "New schedule collided with old release.");
            Check((await newStore.List()).Count == 1 && (await oldStore.List()).Count == 1, "Dashboard list crossed a release boundary.");
        }
        finally
        {
            foreach (string version in releases)
            {
                string isolated = new FlywheelOptions { ApplicationVersion = version, IsolateApplicationVersion = true }.GetStorageName(ns);
                string prefix = LibrarianPrefix(isolated);
                IServer server = db.Multiplexer.GetServer((await db.IdentifyEndpointAsync(prefix + "clock"))!);
                await foreach (RedisKey key in server.KeysAsync(pattern: prefix + "*")) await db.KeyDeleteAsync(key);
            }
        }
    });

    [Test]
    public Task ContinuousEnqueuesCannotHoldTheClaimGatePastTheRenewalDeadline() => WithStore(async (writer, db, ns) =>
    {
        IDatabase wrapped = DispatchProxy.Create<IDatabase, ContendedDatabase>();
        var proxy = (ContendedDatabase)wrapped;
        proxy.Inner = db;
        await using var reader = new RedisJobStore(_ => Task.FromResult(wrapped), ns, operationTimeout: TimeSpan.FromMilliseconds(200));
        await writer.Enqueue(Request());
        await writer.Enqueue(Request());
        JobLease running = (await reader.Claim("running", TimeSpan.FromSeconds(3)))!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.AfterRange = async () =>
        {
            entered.TrySetResult();
            await writer.Enqueue(Request() with { Delay = TimeSpan.FromHours(1) });
            await Task.Delay(10);
        };
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<JobLease?> claiming = reader.Claim("contended", TimeSpan.FromSeconds(3), limit.Token);
        try
        {
            await entered.Task.WaitAsync(limit.Token);
            using var renewalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            Check(await reader.Renew(running, TimeSpan.FromSeconds(3), renewalDeadline.Token) == LeaseStatus.Renewed,
                "Contended claim starved a running job's renewal.");
            try { await claiming; throw new Exception("Continuously changing index unexpectedly produced a claim."); }
            catch (TimeoutException) { }
        }
        finally
        {
            proxy.AfterRange = null;
            await limit.CancelAsync();
            try { await claiming; } catch (TimeoutException) { } catch (OperationCanceledException) { }
        }
        Check(await reader.Finish(running, JobOutcome.Succeeded, null, TimeSpan.Zero), "Storage did not recover after contention.");
    });

    public class ContendedDatabase : DispatchProxy
    {
        public IDatabase Inner = null!;
        public Func<Task>? AfterRange;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result = method!.Invoke(Inner, args);
            return method.Name == "ExecuteAsync" && args?[0] is "ZRANGEBYLEX" && args[1] is object[] values &&
                values[0].ToString()!.Contains("flywheel.dispatch:index:", StringComparison.Ordinal)
                ? After((Task<RedisResult>)result!) : result;
        }
        private async Task<RedisResult> After(Task<RedisResult> task)
        {
            RedisResult result = await task;
            if (AfterRange is { } callback) await callback();
            return result;
        }
    }
}
