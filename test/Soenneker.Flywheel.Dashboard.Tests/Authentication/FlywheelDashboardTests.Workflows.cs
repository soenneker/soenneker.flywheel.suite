using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Dashboard.Consumers.Abstract;
using Soenneker.Flywheel.Dashboard.Registrars;
using Soenneker.Flywheel.Memory;
using Soenneker.Hashing.Pbkdf2;
using System.Net;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    [Test]
    public async Task DefinitionManagementAndWorkflowViewsUseProtectedApi(CancellationToken ct)
    {
        var password = Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<System.Text.Json.Serialization.JsonSerializerContext>(TestJsonContext.Default);
        builder.Services.AddFlywheel().AddMemory().AddDashboard(o => o.PasswordPhc = Pbkdf2HashingUtil.Hash(password));
        builder.Services.AddSingleton<IJobInvoker, WorkflowDashboardTestInvoker>();
        builder.Services.RemoveAll<IHostedService>();
        await using var app = builder.Build();
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapFlywheelDashboard();
        await app.StartAsync(ct);
        var store = app.Services.GetRequiredService<MemoryJobStore>();
        await store.AddRecurring("schedule", new("job", "{\"secret\":42}", new(), TimeSpan.Zero), TimeSpan.FromMinutes(1), ct);
        var batch = await store.EnqueueBatch([new("job", "{\"secret\":42}", new(), TimeSpan.Zero)],
            new("job", "{}", new(), TimeSpan.Zero), cancellationToken: ct);

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<NavigationManager>(new RouterTestNavigationManager("https://localhost/", "https://localhost/flywheel"));
        services.AddFlywheelDashboardAsScoped(new Uri("https://localhost/"));
        services.AddSingleton<IJSRuntime, JobActionsTestJsRuntime>();
        services.AddSingleton<IComponentActivator, JobActionsTestActivator>();
        var cookies = new CookieContainer();
        services.AddScoped(_ => new HttpClient(new DashboardBrowserTestHandler(app.GetTestServer().CreateHandler(), cookies)) { BaseAddress = new Uri("https://localhost/") });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var consumer = scope.ServiceProvider.GetRequiredService<IFlywheelDashboardConsumer>();
        Check((await consumer.GetScheduleDefinition("schedule", ct)).StatusCode == 401, "Definition payload exposed anonymously.");
        Check((await consumer.GetWorkflow(batch.JobIds[0], ct)).StatusCode == 401, "Workflow exposed anonymously.");
        Check((await consumer.Login("admin", password, ct)).Succeeded, "Login failed.");
        var definition = (await consumer.GetScheduleDefinition("schedule", ct)).Value!.Definition;
        Check(definition.Payload.Contains("secret"), "Editor lost the payload.");
        using (var raw = new HttpClient(app.GetTestServer().CreateHandler()) { BaseAddress = new Uri("https://localhost/") })
        {
            raw.DefaultRequestHeaders.Add("Cookie", cookies.GetCookieHeader(new Uri("https://localhost/")));
            Check((await raw.PostAsync("/flywheel/jobs/schedules/schedule/delete", null, ct)).StatusCode == HttpStatusCode.BadRequest, "Definition mutation bypassed CSRF.");
            string json = await raw.GetStringAsync($"/flywheel/jobs/{batch.JobIds[0]}/workflow", ct);
            Check(!json.Contains("secret") && !json.Contains("payload") && !json.Contains("token"), "Workflow exposed credentials or payloads.");
        }
        Check((await consumer.SetScheduleEnabled("schedule", false, definition.ExpectedRevision!.Value, ct)).Succeeded, "Pause rejected.");
        Check(!(await consumer.GetSchedules(ct)).Value!.Recurring.Single().Enabled, "Paused definition disappeared from dashboard.");
        Check((await consumer.UpdateSchedule("schedule", definition, ct)).StatusCode == 409, "Stale edit accepted.");
        definition = (await consumer.GetScheduleDefinition("schedule", ct)).Value!.Definition;
        Check((await consumer.UpdateSchedule("schedule", definition with { IntervalMilliseconds = 0, Cron = "invalid" }, ct)).StatusCode == 400, "Invalid cron accepted.");
        Check((await consumer.UpdateSchedule("schedule", definition with { IntervalMilliseconds = 2000, CoalesceOccurrences = true,
            Policy = definition.Policy with { MaxStaleRestarts = 2 } }, ct)).Succeeded, "Valid edit rejected.");
        var saved = (await consumer.GetScheduleDefinition("schedule", ct)).Value!.Definition;
        Check(saved.CoalesceOccurrences && saved.Policy.MaxStaleRestarts == 2, "Reliability settings did not round-trip through the API.");
        Check((await consumer.GetSchedules(ct)).Value!.Recurring.Single().CoalesceOccurrences, "Schedule view lost its overlap policy.");
        var workflow = (await consumer.GetWorkflow(batch.JobIds[0], ct)).Value!;
        Check(workflow.Total == 2 && workflow.Completed == 0 && workflow.Steps[1].PredecessorIds.Single() == batch.JobIds[0], "Workflow graph incorrect.");

        await using (var renderer = new HtmlRenderer(scope.ServiceProvider, provider.GetRequiredService<ILoggerFactory>()))
        {
            string html = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var component = await renderer.RenderComponentAsync<WorkflowPanel>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(WorkflowPanel.JobId)] = batch.JobIds[0] }));
                return component.ToHtmlString();
            });
            Check(html.Contains("0 of 2 steps finished") && html.Contains("Waiting for predecessors") && html.Contains("Step 1"), "Workflow rendering omitted dependency or progress.");
            string controls = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var component = await renderer.RenderComponentAsync<DefinitionActions>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(DefinitionActions.Id)] = "schedule", [nameof(DefinitionActions.Enabled)] = false }));
                return component.ToHtmlString();
            });
            Check(controls.Contains("Resume schedule") && controls.Contains("Edit definition") && controls.Contains("Delete definition"), "Schedule controls missing.");
        }
        definition = (await consumer.GetScheduleDefinition("schedule", ct)).Value!.Definition;
        Check((await consumer.DeleteSchedule("schedule", definition.ExpectedRevision!.Value, ct)).Succeeded, "Delete rejected.");
        Check((await consumer.GetScheduleDefinition("schedule", ct)).StatusCode == 404, "Deleted definition still exposed.");
    }
}
