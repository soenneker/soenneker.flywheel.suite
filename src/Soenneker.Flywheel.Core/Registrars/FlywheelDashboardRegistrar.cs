using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Dashboard;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Core.Dashboard.Controllers;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Hashing.Pbkdf2;

namespace Soenneker.Flywheel.Core.Registrars;

/// <summary>Registers dashboard controllers, authentication, and live notifications.</summary>
public static class FlywheelDashboardRegistrar
{
    private const string Scheme = "Flywheel";
    private const string Policy = "FlywheelDashboard";

    /// <summary>Registers credential authentication, CSRF protection and live dashboard invalidation. HTTPS is required.</summary>
    public static FlywheelBuilder AddDashboard(this FlywheelBuilder builder, Action<DashboardOptions> configure)
    {
        var options = new DashboardOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.Username) || options.Username.Length > 128 ||
            string.IsNullOrWhiteSpace(options.PasswordPhc))
            throw new InvalidOperationException(
                "Configure Flywheel dashboard Username and PasswordPhc using a secret provider.");

        if (!Pbkdf2HashingUtil.IsValidPhc(options.PasswordPhc))
            throw new InvalidOperationException("Invalid dashboard password PHC string.");

        builder.Services.AddSingleton(options);
        var originPolicy = new DashboardOriginPolicy(options.AllowedOrigins);
        builder.Services.AddSingleton(originPolicy);
        builder.Services.AddCors(cors => cors.AddPolicy("FlywheelDashboardOrigins", policy => policy
            .SetIsOriginAllowed(originPolicy.IsAllowedCrossOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));
        builder.Services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-Flywheel-CSRF";
            o.Cookie.Name = "__Host-Flywheel-CSRF";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.AddAuthentication().AddCookie(Scheme, o =>
        {
            o.Cookie.Name = "__Host-Flywheel";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = false;
            o.Events.OnRedirectToLogin = c =>
            {
                c.Response.StatusCode = 401;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = c =>
            {
                c.Response.StatusCode = 403;
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorization(o =>
            o.AddPolicy(Policy, p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()));
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            // A global per-node limiter also bounds expensive password hashing with attacker-controlled IPs.
            o.AddFixedWindowLimiter("FlywheelLogin", x =>
            {
                x.PermitLimit = 10;
                x.Window = TimeSpan.FromMinutes(1);
                x.QueueLimit = 0;
            });
        });
        builder.Services.AddControllers().AddApplicationPart(typeof(FlywheelAuthenticationController).Assembly);
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<DashboardSubscriptions>();
        builder.Services.AddSingleton<IDashboardSnapshotFactory, DashboardSnapshotFactory>();
        builder.Services.AddHostedService<DashboardNotifications>();
        return builder;
    }

    /// <summary>Maps the authenticated Flywheel dashboard real-time endpoint.</summary>
    public static IEndpointConventionBuilder MapFlywheelDashboard(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _ = endpoints.ServiceProvider.GetRequiredService<DashboardOptions>();

        return endpoints.MapHub<FlywheelHub>("/flywheel/hub", options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization(Policy);
    }

    /// <summary>Applies the dashboard origin allowlist to API requests and WebSocket handshakes, and enables credentialed CORS. Call after routing and trusted forwarded headers, before authentication and authorization. Requests without Origin still require the dashboard's authentication and antiforgery checks.</summary>
    public static IApplicationBuilder UseFlywheelDashboard(this IApplicationBuilder app)
    {
        var policy = app.ApplicationServices.GetRequiredService<DashboardOriginPolicy>();
        return app.UseWhen(context => context.Request.Path.StartsWithSegments("/flywheel"), branch =>
        {
            branch.Use(async (context, next) =>
            {
                if (!policy.IsAllowed(context.Request))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next(context);
            });
            branch.UseCors("FlywheelDashboardOrigins");
        });
    }
}
