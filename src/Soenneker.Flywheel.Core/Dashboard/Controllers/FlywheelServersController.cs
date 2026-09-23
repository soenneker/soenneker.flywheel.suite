using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Soenneker.Flywheel.Core.Dashboard.Filters;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard.Controllers;

/// <summary>Reads live Flywheel worker servers and their active executions.</summary>
[ApiController]
[Tags("Flywheel servers")]
[Produces("application/json")]
[Route("[flywheel]/servers")]
[Authorize(Policy = "FlywheelDashboard")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[TypeFilter(typeof(DashboardAntiforgeryFilter))]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class FlywheelServersController(IServerStore nodes, IDashboardSnapshotFactory snapshots) : ControllerBase
{
    /// <summary>Returns up to 200 servers with unexpired heartbeats.</summary>
    [HttpGet]
    [EndpointSummary("Returns up to 200 servers with unexpired heartbeats.")]
    [EndpointDescription("Returns at most 200 workers whose heartbeats have not expired, including their public execution snapshots.")]
    [ProducesResponseType(typeof(IReadOnlyList<Communication.Responses.ServerView>), StatusCodes.Status200OK)]
    public async ValueTask<IReadOnlyList<Communication.Responses.ServerView>> List(CancellationToken cancellationToken) =>
        (await nodes.ListServers(200, cancellationToken)).Select(snapshots.Server).ToArray();

    /// <summary>Returns one live server and the jobs currently leased to it.</summary>
    [HttpGet("{id}")]
    [EndpointSummary("Returns one live server and the jobs currently leased to it.")]
    [EndpointDescription("Returns a live worker and its leased jobs. The identifier must be nonblank and at most 200 characters.")]
    [ProducesResponseType(typeof(Communication.Responses.ServerView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async ValueTask<IActionResult> Get([FromRoute] string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) return BadRequest();
        WorkerServerView? server = await nodes.GetServer(id, cancellationToken);
        return server is null ? NotFound() : Ok(snapshots.Server(server));
    }

}
