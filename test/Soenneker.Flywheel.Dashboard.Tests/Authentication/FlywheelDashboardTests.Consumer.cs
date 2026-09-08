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

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    [Test]
    public async Task ConsumerUsesCoreCookieCsrfAndSharedContracts()
    {
        var password = Guid.NewGuid().ToString("N");
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddFlywheel().AddDashboard(o => o.PasswordPhc = Pbkdf2HashingUtil.Hash(password));
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddSingleton<IJobStore>(new SearchStore());
        await using WebApplication app = builder.Build();
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers();
        app.MapFlywheelDashboard();
        await app.StartAsync();

        IServiceCollection services = new ServiceCollection().AddLogging();
        services.AddSingleton<NavigationManager>(new RouterTestNavigationManager("https://dashboard.example/", "https://dashboard.example/"));
        services.AddFlywheelDashboardAsScoped(new Uri("https://backend.example/"), options => options.HomePath = "/");
        services.AddScoped(_ => new HttpClient(new DashboardBrowserTestHandler(app.GetTestServer().CreateHandler())) { BaseAddress = new Uri("https://backend.example/") });
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
