using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Soenneker.Dtos.Results.Operation;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;
using Soenneker.Flywheel.Dashboard.Communication.Abstract;
using Soenneker.Flywheel.Dashboard.Consumers.Abstract;
using Soenneker.Flywheel.Dashboard.Registrars;
using Soenneker.Hashing.Pbkdf2;
using Soenneker.SignalR.Web.Clients.Abstract;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    [Test]
    [Arguments("/flywheel", "/flywheel")]
    [Arguments("/", "/")]
    [Arguments("/", "/operations/engine")]
    [Arguments("/operations/dashboard", "/")]
    [Arguments("/operations/dashboard", "/operations/engine")]
    public async Task ConsumerUsesCoreCookieCsrfAndSharedContracts(string homePath, string enginePath)
    {
        var password = Guid.NewGuid().ToString("N");
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
        builder.WebHost.UseTestServer();
        builder.Services.AddFlywheel().AddDashboard(o => { o.EnginePath = enginePath; o.PasswordPhc = Pbkdf2HashingUtil.Hash(password); });
        builder.Services.RemoveAll<IHostedService>();
        var store = new SearchStore();
        builder.Services.AddSingleton<IJobStore>(store);
        builder.Services.AddSingleton<IJobLogStore>(store);
        builder.Services.AddSingleton<INodeStore>(store);
        builder.Services.AddSingleton<IServerStore>(store);
        await using WebApplication app = builder.Build();
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers();
        app.MapFlywheelDashboard();
        await app.StartAsync();

        IServiceCollection services = new ServiceCollection().AddLogging();
        services.AddSingleton<NavigationManager>(new RouterTestNavigationManager("https://dashboard.example/", "https://dashboard.example/"));
        services.AddFlywheelDashboardAsScoped(new Uri("https://backend.example/"), options => { options.HomePath = homePath; options.EnginePath = enginePath; });
        var cookies = new System.Net.CookieContainer();
        services.AddScoped(_ => new HttpClient(new DashboardBrowserTestHandler(app.GetTestServer().CreateHandler(), cookies)) { BaseAddress = new Uri("https://backend.example/") });
        services.AddScoped<ISignalRWebClients>(_ => new DashboardTestSignalRClients(() => new DashboardBrowserTestHandler(app.GetTestServer().CreateHandler(), cookies)));
        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        var consumer = scope.ServiceProvider.GetRequiredService<IFlywheelDashboardConsumer>();

        OperationResult<SearchResult> anonymous = await consumer.Search();
        Check(anonymous.StatusCode == 401 && anonymous.Failed, "Anonymous consumer result lost authentication failure.");
        OperationResult<object> wrong = await consumer.Login("admin", "wrong");
        Check(wrong.StatusCode == 401 && wrong.Failed, "Incorrect credentials were accepted.");
        OperationResult<object> login = await consumer.Login("admin", password);
        Check(login.Succeeded && login.StatusCode == 204, "Consumer did not complete CSRF-protected login.");
        OperationResult<SearchResult> search = await consumer.Search("invoice&monthly", 50, 25);
        Check(search.Succeeded && search.Value?.TotalCount == 51, "Consumer failed to deserialize the shared search contract.");
        OperationResult<List<ServerView>> servers = await consumer.GetServers();
        Check(servers.Succeeded && servers.Value is [{ Id: "node one", Workers: 12 }], "Server list did not load at the configured base path.");
        OperationResult<ServerView> server = await consumer.GetServer("node one");
        Check(server.Succeeded && server.Value is { Id: "node one", Workers: 12 }, "Server detail did not load at the configured base path.");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var received = new TaskCompletionSource<LiveBoard>(TaskCreationOptions.RunContinuationsAsynchronously);
            var live = scope.ServiceProvider.GetRequiredService<IFlywheelLiveClient>();
            await using IFlywheelLiveSubscription subscription = await live.Board("prefix-test", board =>
            {
                received.TrySetResult(board);
                return Task.CompletedTask;
            }, () => Task.CompletedTask, () => Task.CompletedTask, timeout.Token);
            await subscription.Start(timeout.Token);
            await subscription.SubscribeBoard(1, "invoice", 0, 25, null, null, timeout.Token);
            await received.Task.WaitAsync(timeout.Token);
            Check(subscription.IsConnected, "SignalR did not connect using the independent engine prefix.");
        }
        using HttpResponseMessage obsolete = await scope.ServiceProvider.GetRequiredService<IFlywheelApiClient>()
            .Get(enginePath == "/" ? "flywheel/servers" : "servers");
        Check(obsolete.StatusCode == System.Net.HttpStatusCode.NotFound, "An endpoint remained mapped outside the configured base path.");
        OperationResult<JobView> job = await consumer.GetJob("one");
        Check(job.Value is { MaxAttempts: 5, Priority: "Normal" }, "Shared execution projection was lost.");
        OperationResult<JobView> missing = await consumer.GetJob("missing");
        Check(missing.StatusCode == 404 && missing.Failed, "Missing execution did not preserve failure status.");
        OperationResult<ScheduleView> schedules = await consumer.GetSchedules();
        Check(schedules.StatusCode == 501 && schedules.Failed, "Unsupported store capability did not preserve failure status.");
        OperationResult<object> logout = await consumer.Logout();
        Check(logout.Succeeded, "Consumer did not refresh CSRF after authentication changed.");
        Check((await consumer.Search()).StatusCode == 401, "Sign-out left an authenticated consumer session.");

        var api = scope.ServiceProvider.GetRequiredService<IFlywheelApiClient>();
        await Assert.That(async () => await api.Get("https://untrusted.example/flywheel/jobs")).Throws<InvalidOperationException>();
        await app.StopAsync();
    }
}
