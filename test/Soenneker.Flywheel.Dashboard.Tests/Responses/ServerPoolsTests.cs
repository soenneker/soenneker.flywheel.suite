using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Tests.Responses;

public sealed class ServerPoolsTests
{
    [Test]
    public async Task DisplaysIndependentBusyCountsAndPausedPools()
    {
        var server = new ServerView("server", 1, 4,
            [Job("one", "X"), Job("two", "X"), Job("three", "Y")])
        {
            Pools = [new("X", 3), new("Y", 1), new("paused", 0)]
        };
        string html = await Render(server);
        if (!html.Contains("X: 2 / 3") || !html.Contains("Y: 1 / 1") || !html.Contains("paused: 0 / 0"))
            throw new Exception("Server pool capacities and busy counts did not render correctly.");
    }

    [Test]
    public async Task DisplaysLegacyServersAsDefaultPool()
    {
        string html = await Render(new ServerView("legacy", 1, 4, [Job("one", null)]));
        if (!html.Contains("default: 1 / 4")) throw new Exception("Legacy server capacity was not displayed.");
    }

    private static JobView Job(string id, string? pool) => new(id, "job", "Running", 1, 1, false, null, 1, 1, 1, 1, "server", WorkerPool: pool);

    private static async Task<string> Render(ServerView server)
    {
        await using ServiceProvider services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ServerPools>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(ServerPools.Server)] = server }));
            return component.ToHtmlString();
        });
    }
}
