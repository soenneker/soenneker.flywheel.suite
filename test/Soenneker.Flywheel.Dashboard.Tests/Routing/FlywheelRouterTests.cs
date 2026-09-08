using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soenneker.Flywheel.Dashboard.Registrars;
using Soenneker.Flywheel.Dashboard.Communication.Abstract;
using Soenneker.Flywheel.Communication.Responses;

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
    [Arguments("/", "flywheel/recurring/daily%20one", "Schedule", "daily one")]
    [Arguments("/", "flywheel/recurring/a%252Fb", "Schedule", "a%2Fb")]
    [Arguments("/", "flywheel/recurring/a/b", "NotFound", null)]
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
        DashboardRoute route = DashboardRoute.Match(relativePath, homePath);
        Check(route.Page.Name == expectedPage && route.Id == expectedId,
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
        }, authenticated: true);
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
        }, authenticated: true);
    }

    [Test]
    public async Task JobParameterIsPassedToConcretePage()
    {
        await VerifyRendering("/flywheel", "https://example.test/", "https://example.test/jobs/job%201", (component, navigation, handler) =>
        {
            Check(handler.Paths.Contains("/flywheel/jobs/job%201"), "Job ID was not passed to the concrete page.");
            return Task.CompletedTask;
        }, authenticated: true);
    }

    [Test]
    public async Task SchedulePagesShareLayoutAndConnectionWithoutLoadingOverview()
    {
        var live = new BoardConnectionTestClient();
        await VerifyRendering("/", "https://example.test/", "https://example.test/flywheel/recurring", async (component, navigation, handler) =>
        {
            Check(component.ToHtmlString().Contains("Search recurring jobs"), "Recurring page did not render.");
            Check(!component.ToHtmlString().Contains("Search scheduled jobs"), "Recurring page rendered scheduled content.");
            Check(!component.ToHtmlString().Contains("Activity chart"), "Recurring page rendered the overview chart.");
            Check(live.Connections == 1, "The layout did not start its connection.");
            navigation.NavigateTo("/flywheel/scheduled");
            await component.QuiescenceTask;
            Check(component.ToHtmlString().Contains("Search scheduled jobs"), "Scheduled page did not render.");
            Check(!component.ToHtmlString().Contains("Search recurring jobs"), "The previous page remained mounted.");
            navigation.NavigateTo("/flywheel/servers");
            await component.QuiescenceTask;
            Check(live.Connections == 1 && !live.Transport.Disposed, "Navigation recreated the layout connection.");
            Check(handler.Paths.Count(path => path.EndsWith("/jobs/search")) == 1, "Navigation repeated the layout authentication check or loaded overview executions.");
            Check(!handler.Paths.Any(path => path.Contains("/history")), "Schedule navigation loaded overview history.");
            Check(component.ToHtmlString().Contains("Connected"), "The persistent header lost its connection status.");
            navigation.NavigateTo("/");
            await component.QuiescenceTask;
            Check(component.ToHtmlString().Contains("Activity chart"), "The overview page did not render its own chart.");
            Check(!handler.Paths.Any(path => path.EndsWith("/jobs/history")), "The live overview loaded five-minute historical buckets.");
            navigation.NavigateTo("/flywheel/recurring");
            await component.QuiescenceTask;
            Check(!component.ToHtmlString().Contains("Activity chart"), "The overview remained mounted on a schedule page.");
            Check(live.Connections == 1 && !live.Transport.Disposed, "Leaving the overview disposed the layout's connection.");
        }, authenticated: true, live: live);
        Check(live.Transport.Disposed, "Disposing the layout left its socket open.");
    }

    [Test]
    public async Task SessionExpiryRedirectsToStandaloneSignInAndClosesTheLayoutConnection()
    {
        var live = new BoardConnectionTestClient();
        await VerifyRendering("/", "https://example.test/", "https://example.test/flywheel/recurring", async (component, navigation, handler) =>
        {
            handler.Authenticated = false;
            navigation.NavigateTo("jobs/expired");
            await component.QuiescenceTask;
            Check(navigation.Uri == "https://example.test/signin", "Expired session did not redirect to the sign-in page.");
            Check(live.Transport.Disposed, "Expired session retained the connection.");
            Check(component.ToHtmlString().Contains("flywheel-password"), "Standalone sign-in form was not rendered.");
            Check(!component.ToHtmlString().Contains("Search recurring jobs"), "Protected page content remained mounted.");
            Check(!handler.Paths.Any(path => path.Contains("/history")), "Sign-in loaded overview history.");
        }, authenticated: true, live: live);
    }

    [Test]
    public async Task ScheduleDetailsUseTheSelectedIdAndReuseTheLayoutOnNavigation()
    {
        var live = new BoardConnectionTestClient();
        var schedules = new ScheduleView([
            new("daily one", "Daily report", 60000, 0),
            new("weekly%2Ftwo", "Weekly cleanup", 3600000, 0)
        ], []);
        await VerifyRendering("/", "https://example.test/", "https://example.test/flywheel/recurring/daily%20one", async (component, navigation, handler) =>
        {
            Check(component.ToHtmlString().Contains("Daily report"), "Schedule detail did not resolve the selected schedule.");
            Check(!component.ToHtmlString().Contains("Weekly cleanup"), "Schedule detail rendered other schedules.");
            navigation.NavigateTo("/flywheel/recurring/weekly%252Ftwo");
            await component.QuiescenceTask;
            Check(component.ToHtmlString().Contains("Weekly cleanup"), "Schedule detail did not update its route parameter.");
            Check(!component.ToHtmlString().Contains("Daily report"), "The previous schedule remained visible.");
            navigation.NavigateTo("/flywheel/recurring/missing");
            await component.QuiescenceTask;
            Check(component.ToHtmlString().Contains("Schedule unavailable"), "An absent schedule displayed stale details.");
            Check(live.Connections == 1 && !live.Transport.Disposed, "Detail navigation recreated the layout connection.");
            Check(handler.Paths.Count(path => path.EndsWith("/jobs/schedules")) == 1, "Detail navigation unnecessarily reloaded the schedule list.");
        }, authenticated: true, live: live, schedules: schedules);
    }

    [Test]
    public async Task LiveOverviewRendersSecondResolutionActivityFromPush()
    {
        var live = new BoardConnectionTestClient();
        await VerifyRendering("/", "https://example.test/", "https://example.test/", async (component, navigation, handler) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000 * 1000;
            List<JobHistoryPoint> points = Enumerable.Range(0, 61)
                .Select(index => new JobHistoryPoint(now - 60000 + index * 1000, 0, index == 60 ? 1 : 0, index == 60 ? 1 : 0, 0)).ToList();
            await live.Snapshot(new LiveBoard(live.Transport.Version, [], 0, null, null, LiveActivity: points));
            string html = component.ToHtmlString();
            Check(html.Contains("Started"), "The live chart omitted job starts");
            Check(html.Contains("data-scroll-enabled=\"true\"") && html.Contains("data-scroll-duration=\"1000\""),
                "Live activity must use one-second Quark scrolling");
            Check(html.Contains($"data-scroll-x-min=\"{now - 60000}\""), "The chart did not retain a full minute of live samples");
            Check(!handler.Paths.Any(path => path.EndsWith("/jobs/history")), "Live updates fetched historical aggregates");
        }, authenticated: true, live: live);
    }

    private static async Task VerifyRendering(string homePath, string baseUri, string uri,
        Func<Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent, NavigationManager, RouterTestHttpHandler, Task> verify,
        bool authenticated = false, BoardConnectionTestClient? live = null, ScheduleView? schedules = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlywheelDashboardAsScoped(options => options.HomePath = homePath);
        var navigation = new RouterTestNavigationManager(baseUri, uri);
        using var handler = new RouterTestHttpHandler(authenticated);
        if (schedules is not null) handler.Schedules = schedules;
        services.AddSingleton<IFlywheelLiveClient>(live ?? new BoardConnectionTestClient());
        services.AddSingleton(new HttpClient(handler) { BaseAddress = new Uri(baseUri) });
        services.AddSingleton<NavigationManager>(navigation);
        services.AddSingleton<INavigationInterception, RouterTestNavigationInterception>();
        services.AddSingleton<IScrollToLocationHash, RouterTestScrollToLocationHash>();
        services.AddSingleton<IJSRuntime, RouterTestJsRuntime>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            ParameterView parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(FlywheelRouter.NotFound)] = (RenderFragment<string>)(path => builder => builder.AddContent(0, "host:" + path))
            });
            HtmlRootComponent component = await renderer.RenderComponentAsync<FlywheelRouter>(parameters);
            await verify(component, navigation, handler);
        });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
