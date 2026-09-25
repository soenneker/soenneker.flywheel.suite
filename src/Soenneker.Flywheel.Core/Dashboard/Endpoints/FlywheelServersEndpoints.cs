using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard.Endpoints;

/// <summary>Reads live Flywheel worker servers and their active executions.</summary>
public static class FlywheelServersEndpoints
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/servers", List)
            .WithName("FlywheelServers_List")
            .WithTags("Flywheel servers")
            .WithSummary("Returns up to 200 servers with unexpired heartbeats.")
            .WithDescription("Returns at most 200 workers whose heartbeats have not expired, including their public execution snapshots.")
            .Produces<IReadOnlyList<Communication.Responses.ServerView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/servers/{id}", Get)
            .WithName("FlywheelServers_Get")
            .WithTags("Flywheel servers")
            .WithSummary("Returns one live server and the jobs currently leased to it.")
            .WithDescription("Returns a live worker and its leased jobs. The identifier must be nonblank and at most 200 characters.")
            .Produces<Communication.Responses.ServerView>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }

    /// <summary>Returns up to 200 servers with unexpired heartbeats.</summary>
    public static async ValueTask<IResult> List([FromServices] IServerStore nodes, [FromServices] IDashboardSnapshotFactory snapshots, CancellationToken cancellationToken) =>
        DashboardResults.Json((await nodes.ListServers(200, cancellationToken)).Select(snapshots.Server).ToList());

    /// <summary>Returns one live server and the jobs currently leased to it.</summary>
    public static async ValueTask<IResult> Get([FromServices] IServerStore nodes, [FromServices] IDashboardSnapshotFactory snapshots, [FromRoute] string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) return TypedResults.BadRequest();
        WorkerServerView? server = await nodes.GetServer(id, cancellationToken);
        return server is null ? TypedResults.NotFound() : DashboardResults.Json(snapshots.Server(server));
    }

}
