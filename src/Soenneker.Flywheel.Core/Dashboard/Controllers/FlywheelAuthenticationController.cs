using Microsoft.AspNetCore.Http;
using Soenneker.Flywheel.Core.Dashboard.Filters;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Hashing.Pbkdf2;

namespace Soenneker.Flywheel.Core.Dashboard.Controllers;

/// <summary>Authenticates dashboard users and issues antiforgery tokens.</summary>
[ApiController]
[Tags("Flywheel authentication")]
[Produces("application/json")]
[Route("[flywheel]")]
[Authorize(Policy = "FlywheelDashboard")]
[TypeFilter(typeof(DashboardAntiforgeryFilter))]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class FlywheelAuthenticationController(DashboardOptions options, IAntiforgery antiforgery) : ControllerBase
{
    /// <summary>Returns the authenticated dashboard user.</summary>
    [HttpGet("user")]
    [EndpointSummary("Returns the authenticated dashboard user.")]
    [EndpointDescription("Returns the username associated with the authenticated dashboard cookie.")]
    [ProducesResponseType(typeof(DashboardUser), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<DashboardUser> GetUser() => Ok(new DashboardUser(User.Identity!.Name!));

    /// <summary>Issues an antiforgery token for the current dashboard identity.</summary>
    [HttpGet("csrf")]
    [AllowAnonymous]
    [EndpointSummary("Issues an antiforgery token for the current dashboard identity.")]
    [EndpointDescription("Available anonymously. Issues an antiforgery cookie and request token for the current dashboard identity. Use the request token for dashboard POST requests.")]
    [ProducesResponseType(typeof(Csrf), StatusCodes.Status200OK)]
    public async ValueTask<IActionResult> GetCsrf()
    {
        AuthenticateResult auth = await HttpContext.AuthenticateAsync("Flywheel");
        if (auth.Principal is not null)
            HttpContext.User = auth.Principal;
        return Ok(new Csrf(antiforgery.GetAndStoreTokens(HttpContext).RequestToken!));
    }

    /// <summary>Verifies credentials and issues the secure dashboard cookie.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("FlywheelLogin")]
    [EndpointSummary("Verifies credentials and issues the secure dashboard cookie.")]
    [EndpointDescription("Accepts dashboard credentials and issues an authentication cookie. Requires an antiforgery token. Invalid credentials return 401; exceeding the login rate limit returns 429.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async ValueTask<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (request.Username is null || request.Password is null || request.Username.Length > 128 || request.Password.Length > 1024)
            return Unauthorized();
        if (!Pbkdf2HashingUtil.Verify(request.Password, options.PasswordPhc) ||
            !string.Equals(request.Username, options.Username, StringComparison.Ordinal))
            return Unauthorized();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, options.Username)], "Flywheel");
        await HttpContext.SignInAsync("Flywheel", new ClaimsPrincipal(identity));
        return NoContent();
    }

    /// <summary>Removes the current dashboard authentication cookie.</summary>
    [HttpPost("logout")]
    [EndpointSummary("Removes the current dashboard authentication cookie.")]
    [EndpointDescription("Removes the dashboard authentication cookie. Requires an authenticated dashboard user and an antiforgery token.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async ValueTask<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("Flywheel");
        return NoContent();
    }
}
