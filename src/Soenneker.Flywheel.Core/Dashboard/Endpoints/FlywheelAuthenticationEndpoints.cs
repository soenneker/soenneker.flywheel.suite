using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Hashing.Pbkdf2;

namespace Soenneker.Flywheel.Core.Dashboard.Endpoints;

/// <summary>Authenticates dashboard users and issues antiforgery tokens.</summary>
public static class FlywheelAuthenticationEndpoints
{
    internal static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/user", GetUser)
            .WithName("FlywheelAuthentication_GetUser")
            .WithTags("Flywheel authentication")
            .WithSummary("Returns the authenticated dashboard user.")
            .WithDescription("Returns the username associated with the authenticated dashboard cookie.")
            .Produces<DashboardUser>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/csrf", GetCsrf)
            .WithName("FlywheelAuthentication_GetCsrf")
            .WithTags("Flywheel authentication")
            .WithSummary("Issues an antiforgery token for the current dashboard identity.")
            .WithDescription("Available anonymously. Issues an antiforgery cookie and request token for the current dashboard identity. Use the request token for dashboard POST requests.")
            .Produces<Csrf>(StatusCodes.Status200OK)
            .AllowAnonymous();
        group.MapPost("/login", Login)
            .WithName("FlywheelAuthentication_Login")
            .WithTags("Flywheel authentication")
            .WithSummary("Verifies credentials and issues the secure dashboard cookie.")
            .WithDescription("Accepts dashboard credentials and issues an authentication cookie. Requires an antiforgery token. Invalid credentials return 401; exceeding the login rate limit returns 429.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .AllowAnonymous()
            .RequireRateLimiting("FlywheelLogin")
            .Accepts<LoginRequest>("application/json");
        group.MapPost("/logout", Logout)
            .WithName("FlywheelAuthentication_Logout")
            .WithTags("Flywheel authentication")
            .WithSummary("Removes the current dashboard authentication cookie.")
            .WithDescription("Removes the dashboard authentication cookie. Requires an authenticated dashboard user and an antiforgery token.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }

    /// <summary>Returns the authenticated dashboard user.</summary>
    public static IResult GetUser(HttpContext httpContext) => DashboardResults.Json(new DashboardUser(httpContext.User.Identity!.Name!));

    /// <summary>Issues an antiforgery token for the current dashboard identity.</summary>
    public static async ValueTask<IResult> GetCsrf(HttpContext httpContext, [FromServices] IAntiforgery antiforgery)
    {
        AuthenticateResult auth = await httpContext.AuthenticateAsync("Flywheel");
        if (auth.Principal is not null)
            httpContext.User = auth.Principal;
        return DashboardResults.Json(new Csrf(antiforgery.GetAndStoreTokens(httpContext).RequestToken!));
    }

    /// <summary>Verifies credentials and issues the secure dashboard cookie.</summary>
    public static async ValueTask<IResult> Login(HttpContext httpContext, [FromServices] DashboardOptions options, [FromBody] LoginRequest request)
    {
        if (request.Username is null || request.Password is null || request.Username.Length > 128 || request.Password.Length > 1024)
            return TypedResults.Unauthorized();
        if (!Pbkdf2HashingUtil.Verify(request.Password, options.PasswordPhc) ||
            !string.Equals(request.Username, options.Username, StringComparison.Ordinal))
            return TypedResults.Unauthorized();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, options.Username)], "Flywheel");
        await httpContext.SignInAsync("Flywheel", new ClaimsPrincipal(identity));
        return TypedResults.NoContent();
    }

    /// <summary>Removes the current dashboard authentication cookie.</summary>
    public static async ValueTask<IResult> Logout(HttpContext httpContext)
    {
        await httpContext.SignOutAsync("Flywheel");
        return TypedResults.NoContent();
    }
}
