using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Dashboard;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Core.Dashboard.Endpoints;
using Microsoft.AspNetCore.Antiforgery;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Hashing.Pbkdf2;
using Soenneker.Flywheel.Communication;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Soenneker.Flywheel.Core.Registrars;

/// <summary>Registers dashboard endpoints, authentication, and live notifications.</summary>
public static class FlywheelDashboardRegistrar
{
    private const string Scheme = "Flywheel";
    private const string Policy = "FlywheelDashboard";

    /// <summary>Registers credential authentication, CSRF protection and live dashboard invalidation. HTTPS is required.</summary>
    public static FlywheelBuilder AddDashboard(this FlywheelBuilder builder, Action<DashboardOptions> configure)
    {
        var options = new DashboardOptions();
        configure(options);
        options.UsesIndividualAccounts = options.Accounts.Count != 0;
        options.EnginePath = DashboardPaths.Normalize(options.EnginePath);
        if (options.Accounts.Count == 0 && options.AuthenticationSchemes.Length == 0 && (string.IsNullOrWhiteSpace(options.Username) || options.Username.Length > 128 ||
            string.IsNullOrWhiteSpace(options.PasswordPhc)))
            throw new InvalidOperationException(
                "Configure Flywheel dashboard Username and PasswordPhc using a secret provider.");

        if (options.Accounts.Count == 0 && (options.AuthenticationSchemes.Length == 0 || !string.IsNullOrWhiteSpace(options.PasswordPhc)) && !Pbkdf2HashingUtil.IsValidPhc(options.PasswordPhc) ||
            options.Accounts.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 ||
                !Pbkdf2HashingUtil.IsValidPhc(pair.Value.PasswordPhc)))
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
            o.Events.OnValidatePrincipal = async context =>
            {
                if (DashboardIdentity.Valid(options, context.Principal)) return;
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(Scheme);
            };
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
            o.AddPolicy(Policy, p => p.AddAuthenticationSchemes([Scheme, .. options.AuthenticationSchemes])
                .RequireAuthenticatedUser().RequireRole("Flywheel.Reader", "Flywheel.Operator")
                .RequireAssertion(context => !string.IsNullOrWhiteSpace(context.User.Identity?.Name))));
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            // Use only the connection address, after the host's trusted forwarded-header middleware.
            o.AddPolicy("FlywheelLogin", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        });
        builder.Services.AddSingleton<DashboardLoginLimiter>();

        builder.Services.ConfigureHttpJsonOptions(json => FlywheelJsonContext.Configure(json.SerializerOptions));
        builder.Services.AddSingleton<DashboardSessionFilter>();
        builder.Services.AddSignalR().AddHubOptions<FlywheelHub>(o => o.AddFilter<DashboardSessionFilter>()).AddJsonProtocol(json =>
            FlywheelJsonContext.Configure(json.PayloadSerializerOptions));
        builder.Services.AddSingleton<DashboardSubscriptions>();
        builder.Services.AddSingleton<IDashboardSnapshotFactory, DashboardSnapshotFactory>();
        builder.Services.AddHostedService<DashboardNotifications>();
        return builder;
    }

    /// <summary>Maps the Flywheel dashboard HTTP API and authenticated real-time endpoint.</summary>
    public static IEndpointConventionBuilder MapFlywheelDashboard(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        DashboardOptions dashboard = endpoints.ServiceProvider.GetRequiredService<DashboardOptions>();

        RouteGroupBuilder group = endpoints.MapGroup(dashboard.EnginePath.TrimEnd('/'))
            .RequireAuthorization(Policy);
        group.AddEndpointFilter(async (context, next) =>
        {
            HttpContext http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store, no-cache";
            http.Response.Headers.Pragma = "no-cache";
            if (HttpMethods.IsPost(http.Request.Method))
            {
                bool credentials = http.Request.Path == $"{dashboard.EnginePath.TrimEnd('/')}/login" ||
                    http.Request.Path == $"{dashboard.EnginePath.TrimEnd('/')}/logout";
                if (!credentials && !http.User.IsInRole("Flywheel.Operator"))
                {
                    http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Flywheel.Dashboard.Audit")
                        .LogWarning("Dashboard action denied for {Actor} on {Path}, trace {TraceId}",
                            http.User.Identity?.Name, http.Request.Path.Value, http.TraceIdentifier);
                    return TypedResults.StatusCode(StatusCodes.Status403Forbidden);
                }
                try
                {
                    await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
                }
                catch (AntiforgeryValidationException)
                {
                    return TypedResults.BadRequest();
                }
            }
            bool mutation = !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method);
            if (!mutation) return await next(context);
            ILogger audit = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Flywheel.Dashboard.Audit");
            string actor = http.User.Identity?.Name ?? "anonymous";
            string action = http.GetEndpoint()?.DisplayName ?? http.Request.Method;
            try
            {
                object? result = await next(context);
                audit.LogInformation("Dashboard action {Action} by {Actor} on {Path}, status {StatusCode}, trace {TraceId}",
                    action, actor, http.Request.Path.Value, (result as IStatusCodeHttpResult)?.StatusCode ?? 200, http.TraceIdentifier);
                return result;
            }
            catch (Exception)
            {
                audit.LogError("Dashboard action {Action} by {Actor} on {Path} failed, trace {TraceId}",
                    action, actor, http.Request.Path.Value, http.TraceIdentifier);
                throw;
            }
        });
        FlywheelAuthenticationEndpoints.Map(group);
        FlywheelJobsEndpoints.Map(group);
        FlywheelServersEndpoints.Map(group);

        return endpoints.MapHub<FlywheelHub>($"{dashboard.EnginePath.TrimEnd('/')}/hub", options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization(Policy);
    }

    /// <summary>Applies the dashboard origin allowlist to API requests and WebSocket handshakes, and enables credentialed CORS. Call after routing and trusted forwarded headers, before authentication and authorization. Requests without Origin still require the dashboard's authentication and antiforgery checks.</summary>
    public static IApplicationBuilder UseFlywheelDashboard(this IApplicationBuilder app)
    {
        var policy = app.ApplicationServices.GetRequiredService<DashboardOriginPolicy>();
        var dashboard = app.ApplicationServices.GetRequiredService<DashboardOptions>();
        string prefix = dashboard.EnginePath.TrimEnd('/');
        string[] paths = ["csrf", "login", "logout", "user", "jobs", "servers", "hub"];
        return app.UseWhen(context => paths.Any(path => context.Request.Path.StartsWithSegments($"{prefix}/{path}")), branch =>
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
