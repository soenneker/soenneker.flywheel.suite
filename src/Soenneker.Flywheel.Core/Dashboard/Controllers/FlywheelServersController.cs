using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Soenneker.Flywheel.Core.Dashboard.Filters;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard.Controllers;

/// <summary>Reads live Flywheel worker servers and their active executions.</summary>
[ApiController]
[Route("flywheel/servers")]
[Authorize(Policy = "FlywheelDashboard")]
[TypeFilter(typeof(DashboardAntiforgeryFilter))]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class FlywheelServersController(IServerStore nodes, IDashboardSnapshotFactory snapshots) : ControllerBase
{
    /// <summary>Returns up to 200 servers with unexpired heartbeats.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<Communication.Responses.ServerView>> List(CancellationToken cancellationToken) =>
        (await nodes.ListServers(200, cancellationToken)).Select(snapshots.Server).ToArray();

    /// <summary>Returns one live server and the jobs currently leased to it.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) return BadRequest();
        WorkerServerView? server = await nodes.GetServer(id, cancellationToken);
        return server is null ? NotFound() : Ok(snapshots.Server(server));
    }

}
