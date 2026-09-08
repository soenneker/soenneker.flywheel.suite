using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Dashboard.Registrars;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardConnectionTests
{
    [Test]
    [Arguments("https://backend.example/", "/", "https://backend.example/flywheel/hub")]
    [Arguments("https://backend.example/operations", "/", "https://backend.example/operations/flywheel/hub")]
    [Arguments("https://backend.example/operations/", "/flywheel", "https://backend.example/operations/flywheel/hub")]
    public async Task Backend_registration_preserves_base_path_and_home_route(string backend, string home, string hub)
    {
        var services = new ServiceCollection();
        services.AddFlywheelDashboardAsScoped(new Uri(backend), options => options.HomePath = home);
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var http = scope.ServiceProvider.GetRequiredService<HttpClient>();

        await Assert.That(new Uri(http.BaseAddress!, "flywheel/hub").AbsoluteUri).IsEqualTo(hub);
        await Assert.That(provider.GetRequiredService<DashboardNavigationOptions>().HomePath).IsEqualTo(home);
    }

    [Test]
    public async Task Negotiation_and_login_requests_include_browser_cookies()
    {
        using var http = new HttpClient(new DashboardCredentialsHandler(new DashboardCredentialsTestHandler()));
        using var negotiate = await http.PostAsync("https://backend.example/flywheel/hub/negotiate?negotiateVersion=1", null);
        using var login = await http.PostAsync("https://backend.example/flywheel/login", null);
        await Assert.That(negotiate.IsSuccessStatusCode && login.IsSuccessStatusCode).IsTrue();
    }
}
