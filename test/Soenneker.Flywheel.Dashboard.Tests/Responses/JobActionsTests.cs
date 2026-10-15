using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soenneker.Flywheel.Dashboard.Registrars;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class JobActionsTests
{
    [Test]
    [Arguments("Queued", false, false, "Top of queue", "Run now")]
    [Arguments("Scheduled", false, false, "Run now", "Top of queue")]
    [Arguments("Running", false, false, "Cancel job", "Top of queue")]
    [Arguments("Waiting", false, false, "Cancel job", "Run now")]
    [Arguments("Succeeded", false, false, "Run again", "Cancel job")]
    [Arguments("Queued", false, true, "Cancellation requested", "Top of queue")]
    [Arguments("Queued", true, false, "Move latest execution to top", "Cancel job")]
    public async Task MenuShowsOnlyApplicableActions(string state, bool definition, bool cancelling, string expected, string excluded)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlywheelDashboardAsScoped();
        services.AddSingleton(new HttpClient { BaseAddress = new Uri("https://localhost/") });
        services.AddSingleton<NavigationManager>(new RouterTestNavigationManager("https://localhost/", "https://localhost/flywheel"));
        services.AddSingleton<IJSRuntime, JobActionsTestJsRuntime>();
        services.AddSingleton<IComponentActivator, JobActionsTestActivator>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        string html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<JobActions>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(JobActions.Id)] = "execution", [nameof(JobActions.Name)] = "Invoice",
                [nameof(JobActions.State)] = state, [nameof(JobActions.IsDefinition)] = definition,
                [nameof(JobActions.CancelRequested)] = cancelling
            }));
            return component.ToHtmlString();
        });
        if (!html.Contains("Actions for Invoice") || !html.Contains(expected) || html.Contains(excluded))
            throw new Exception($"Unexpected actions for {state}: {html}");
    }
}
