using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Services.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard.Endpoints;

internal static class FlywheelDefinitionEndpoints
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/jobs/{id}/workflow", Workflow).WithName("FlywheelJobs_Workflow").Produces<WorkflowView>();
        group.MapGet("/jobs/schedules/{id}/definition", Definition).WithName("FlywheelJobs_ScheduleDefinition").Produces<ScheduleDefinitionView>();
        group.MapPost("/jobs/schedules/{id}/update", Update).WithName("FlywheelJobs_UpdateSchedule");
        group.MapPost("/jobs/schedules/{id}/enabled/{enabled:bool}", Enable).WithName("FlywheelJobs_EnableSchedule");
        group.MapPost("/jobs/schedules/{id}/delete", Delete).WithName("FlywheelJobs_DeleteSchedule");
        group.MapGet("/jobs/manual/{id}/definition", ManualDefinition).WithName("FlywheelJobs_ManualDefinition").Produces<EnqueueRequest>();
        group.MapPost("/jobs/manual/{id}/update", UpdateManual).WithName("FlywheelJobs_UpdateManual");
        group.MapPost("/jobs/manual/{id}/delete", DeleteManual).WithName("FlywheelJobs_DeleteManual");
    }

    private static bool ValidId(string id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 200;
    private static async Task<IResult> Workflow(string id, [FromServices] IJobStore store, CancellationToken ct)
    {
        if (!ValidId(id)) return TypedResults.BadRequest();
        if (store is not IJobWorkflowStore workflows) return TypedResults.StatusCode(501);
        return await workflows.GetWorkflow(id, ct) is { } view ? DashboardResults.Json(view) : TypedResults.NotFound();
    }
    private static async Task<IResult> Definition(string id, [FromServices] IJobStore store, CancellationToken ct)
    {
        if (!ValidId(id)) return TypedResults.BadRequest();
        if (store is not IJobDefinitionStore definitions) return TypedResults.StatusCode(501);
        return await definitions.GetRecurringDefinition(id, ct) is { } view ? DashboardResults.Json(view) : TypedResults.NotFound();
    }
    private static async Task<IResult> ManualDefinition(string id, [FromServices] IJobStore store, CancellationToken ct)
    {
        if (!ValidId(id)) return TypedResults.BadRequest();
        if (store is not IJobDefinitionStore definitions) return TypedResults.StatusCode(501);
        return await definitions.GetManualDefinition(id, ct) is { } view ? DashboardResults.Json(view) : TypedResults.NotFound();
    }
    private static async Task<IResult> Update(string id, [FromBody] ScheduleUpdate update, [FromServices] IJobStore store,
        [FromServices] IEnumerable<IJobInvoker> invokers, CancellationToken ct)
    {
        if (!ValidId(id) || update.Policy is null || update.Payload is null || !invokers.Any(i => i.Name == update.Name)) return TypedResults.BadRequest();
        if (store is not IJobDefinitionStore definitions) return TypedResults.StatusCode(501);
        try { return await definitions.UpdateRecurring(id, update, ct) ? TypedResults.NoContent() : TypedResults.Conflict(); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
        { return TypedResults.BadRequest(); }
    }
    private static async Task<IResult> Enable(string id, bool enabled, long? revision, [FromServices] IJobStore store, CancellationToken ct)
    {
        if (!ValidId(id)) return TypedResults.BadRequest();
        if (store is not IJobDefinitionStore definitions) return TypedResults.StatusCode(501);
        try { return await definitions.SetRecurringEnabled(id, enabled, revision, ct) ? TypedResults.NoContent() : TypedResults.Conflict(); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
        { return TypedResults.BadRequest(); }
    }
    private static async Task<IResult> Delete(string id, long? revision, [FromServices] IJobStore store, CancellationToken ct)
    {
        if (!ValidId(id)) return TypedResults.BadRequest();
        if (store is not IJobDefinitionStore definitions) return TypedResults.StatusCode(501);
        return await definitions.DeleteRecurring(id, revision, ct) ? TypedResults.NoContent() : TypedResults.Conflict();
    }
    private static async Task<IResult> UpdateManual(string id, [FromBody] EnqueueRequest update, [FromServices] IJobStore store,
        [FromServices] IEnumerable<IJobInvoker> invokers, CancellationToken ct)
    {
        if (!ValidId(id) || update.Policy is null || update.Payload is null || !invokers.Any(i => i.Name == update.Name)) return TypedResults.BadRequest();
        if (store is not IJobDefinitionStore definitions) return TypedResults.StatusCode(501);
        try { return await definitions.UpdateManual(id, update, ct) ? TypedResults.NoContent() : TypedResults.Conflict(); }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException) { return TypedResults.BadRequest(); }
    }
    private static async Task<IResult> DeleteManual(string id, [FromServices] IJobStore store, CancellationToken ct)
    {
        if (!ValidId(id)) return TypedResults.BadRequest();
        if (store is not IJobDefinitionStore definitions) return TypedResults.StatusCode(501);
        return await definitions.DeleteManual(id, ct) ? TypedResults.NoContent() : TypedResults.Conflict();
    }
}
