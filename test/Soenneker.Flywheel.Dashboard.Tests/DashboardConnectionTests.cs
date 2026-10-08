using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Dashboard.Registrars;
using System.Threading;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardConnectionTests
{
    [Test]
    [Arguments("https://backend.example/", "/", "https://backend.example/flywheel/hub")]
    [Arguments("https://backend.example/operations", "/", "https://backend.example/operations/flywheel/hub")]
    [Arguments("https://backend.example/operations/", "/flywheel", "https://backend.example/operations/flywheel/hub")]
    public async ValueTask Backend_registration_preserves_base_path_and_home_route(string backend, string home, string hub, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddFlywheelDashboardAsScoped(new Uri(backend), options => options.HomePath = home);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        var http = scope.ServiceProvider.GetRequiredService<HttpClient>();

        await Assert.That(new Uri(http.BaseAddress!, "flywheel/hub").AbsoluteUri).IsEqualTo(hub);
        await Assert.That(provider.GetRequiredService<DashboardNavigationOptions>().HomePath).IsEqualTo(home);
    }

    [Test]
    public async ValueTask Negotiation_and_login_requests_include_browser_cookies(CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new DashboardCredentialsHandler(new DashboardCredentialsTestHandler()));
        using HttpResponseMessage negotiate = await http.PostAsync("https://backend.example/flywheel/hub/negotiate?negotiateVersion=1", null, cancellationToken: cancellationToken);
        using HttpResponseMessage login = await http.PostAsync("https://backend.example/flywheel/login", null, cancellationToken: cancellationToken);
        await Assert.That(negotiate.IsSuccessStatusCode && login.IsSuccessStatusCode).IsTrue();
    }
}
