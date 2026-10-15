using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Tests.Responses;

public sealed class JobDurationTests
{
    [Test]
    public async Task CancellingExecutionStillDisplaysElapsedTime()
    {
        string html = await Render(Job("Cancelling", DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds(), 0));
        if (!html.Contains(" s</span>")) throw new Exception("Cancelling execution lost its elapsed duration.");
    }

    [Test]
    public async Task CompletedExecutionUsesRecordedEndTime()
    {
        string html = await Render(Job("Succeeded", 1000, 62500));
        if (!html.Contains("1m 1s")) throw new Exception("Completed duration was not based on stored timestamps.");
    }

    private static JobView Job(string state, long start, long end) =>
        new("one", "job", state, 1, 1, false, null, 1, 1, 1, 1, "server", StartedAt: start, CompletedAt: end);

    private static async Task<string> Render(JobView job)
    {
        await using ServiceProvider services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<JobDuration>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(JobDuration.Job)] = job }));
            return component.ToHtmlString();
        });
    }
}
