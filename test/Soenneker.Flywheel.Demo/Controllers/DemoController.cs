using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Soenneker.Flywheel.Core.Dashboard.Filters;
using Soenneker.Flywheel.Demo.Services;

namespace Soenneker.Flywheel.Demo.Controllers;

/// <summary>Starts authenticated demo tours.</summary>
[ApiController]
[Route("demo")]
public sealed class DemoController(DemoTour tour) : ControllerBase
{
    /// <summary>Enqueues another tour for the signed-in dashboard user.</summary>
    [HttpPost("run")]
    [Authorize(Policy = "FlywheelDashboard")]
    [EnableRateLimiting("DemoTour")]
    [TypeFilter(typeof(DashboardAntiforgeryFilter))]
    public async Task<IActionResult> Run(CancellationToken cancellationToken) =>
        Ok(await tour.Run(cancellationToken: cancellationToken));
}
