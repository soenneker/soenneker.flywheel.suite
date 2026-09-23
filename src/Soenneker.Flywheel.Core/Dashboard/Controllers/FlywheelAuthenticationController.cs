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
[Route("[flywheel]")]
[Authorize(Policy = "FlywheelDashboard")]
[TypeFilter(typeof(DashboardAntiforgeryFilter))]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class FlywheelAuthenticationController(DashboardOptions options, IAntiforgery antiforgery) : ControllerBase
{
    /// <summary>Returns the authenticated dashboard user.</summary>
    [HttpGet("user")]
    public ActionResult<DashboardUser> GetUser() => Ok(new DashboardUser(User.Identity!.Name!));

    /// <summary>Issues an antiforgery token for the current dashboard identity.</summary>
    [HttpGet("csrf")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(Csrf), 200)]
    public async Task<IActionResult> GetCsrf()
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
    [ProducesResponseType(204)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
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
    [ProducesResponseType(204)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("Flywheel");
        return NoContent();
    }
}
