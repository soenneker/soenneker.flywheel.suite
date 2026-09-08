using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soenneker.Flywheel.Dashboard.Registrars;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class FlywheelRouterTests
{
    [Test]
    [Arguments("/", "", "Dashboard", null)]
    [Arguments("/", "?view=jobs#activity", "Dashboard", null)]
    [Arguments("/flywheel", "", "NotFound", null)]
    [Arguments("/", "missing", "NotFound", null)]
    [Arguments("/flywheel", "flywheel", "Dashboard", null)]
    [Arguments("/", "FLYWHEEL/", "Dashboard", null)]
    [Arguments("/", "flywheel/recurring", "Recurring", null)]
    [Arguments("/", "flywheel/scheduled", "Scheduled", null)]
    [Arguments("/", "signin", "SignIn", null)]
    [Arguments("/", "jobs/example", "Jobs", "example")]
    [Arguments("/", "jobs/a%20b%2Fc?x=1#log", "Jobs", "a b/c")]
    [Arguments("/", "jobs/a%252Fb", "Jobs", "a%2Fb")]
    [Arguments("/", "jobs/", "NotFound", null)]
    [Arguments("/", "jobs/a/b", "NotFound", null)]
    [Arguments("/", "flywheel/servers", "Servers", null)]
    [Arguments("/", "flywheel/servers/node%201/", "ServerDetails", "node 1")]
    [Arguments("/", "flywheel/servers/a/b", "NotFound", null)]
    public void MatchesExplicitRoutes(string homePath, string relativePath, string expectedPage, string? expectedId)
    {
        var route = DashboardRoute.Match(relativePath, homePath);
        Check(route.Page.ToString() == expectedPage && route.Id == expectedId,
            $"Unexpected route for {relativePath}: {route}.");
    }

    [Test]
    public async Task HostContentHandlesUnmatchedPathsAndNavigation()
    {
        await VerifyRendering("/flywheel", "https://example.test/", "https://example.test/", async (component, navigation, handler) =>
        {
            Check(component.ToHtmlString().Contains("host:"), "Default configuration claimed the host root.");
            navigation.NavigateTo("/custom?view=jobs#heading");
            await component.QuiescenceTask;
            Check(component.ToHtmlString().Contains("host:custom"), "Host content did not receive the new path.");
            Check(handler.Paths.Count == 0, "An unmatched route instantiated a dashboard page.");
        });
    }

    [Test]
    public async Task ConfiguredRootRendersDashboardAndRedirectsAnonymousVisitor()
    {
        await VerifyRendering("/", "https://example.test/", "https://example.test/?view=jobs", (component, navigation, handler) =>
        {
            Check(handler.Paths.Contains("/flywheel/jobs/search"), "Root did not render the dashboard.");
            Check(navigation.Uri == "https://example.test/signin", "Root did not preserve the anonymous sign-in redirect.");
            Check(!component.ToHtmlString().Contains("host:"), "Sign-in fell through to host content.");
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task RoutesRelativeToApplicationBase()
    {
        await VerifyRendering("/flywheel", "https://example.test/operations/", "https://example.test/operations/flywheel/servers", (component, navigation, handler) =>
        {
            Check(handler.Paths.Contains("/operations/flywheel/servers"), "Application base was not respected.");
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task ServerParametersDecodeOnceAndUpdateOnNavigation()
    {
        await VerifyRendering("/flywheel", "https://example.test/", "https://example.test/flywheel/servers/node%20one", async (component, navigation, handler) =>
        {
            Check(handler.Paths.Contains("/flywheel/servers/node%20one"), "Server ID was not passed to the concrete page.");
            navigation.NavigateTo("/flywheel/servers/node%252Ftwo");
            await component.QuiescenceTask;
            Check(handler.Paths.Contains("/flywheel/servers/node%252Ftwo"), "Changed server ID did not load, or was decoded twice.");
        });
    }

    [Test]
    public async Task JobParameterIsPassedToConcretePage()
    {
        await VerifyRendering("/flywheel", "https://example.test/", "https://example.test/jobs/job%201", (component, navigation, handler) =>
        {
            Check(handler.Paths.Contains("/flywheel/jobs/job%201"), "Job ID was not passed to the concrete page.");
            return Task.CompletedTask;
        });
    }

    private static async Task VerifyRendering(string homePath, string baseUri, string uri,
        Func<Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent, NavigationManager, RouterTestHttpHandler, Task> verify)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlywheelDashboardAsScoped(options => options.HomePath = homePath);
        var navigation = new RouterTestNavigationManager(baseUri, uri);
        using var handler = new RouterTestHttpHandler();
        services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri(baseUri) });
        services.AddSingleton<NavigationManager>(navigation);
        services.AddSingleton<INavigationInterception, RouterTestNavigationInterception>();
        services.AddSingleton<IScrollToLocationHash, RouterTestScrollToLocationHash>();
        services.AddSingleton<IJSRuntime, RouterTestJsRuntime>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(FlywheelRouter.NotFound)] = (RenderFragment<string>)(path => builder => builder.AddContent(0, "host:" + path))
            });
            var component = await renderer.RenderComponentAsync<FlywheelRouter>(parameters);
            await verify(component, navigation, handler);
        });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
