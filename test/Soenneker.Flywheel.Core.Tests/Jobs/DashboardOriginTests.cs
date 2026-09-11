using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Registrars;

namespace Soenneker.Flywheel.Core.Tests.Jobs;

public sealed class DashboardOriginTests
{
    private const string PasswordPhc = "$pbkdf2-sha256$i=300000$QtAsVucfLlySNc4h9KewFw$X4zUX9pcEuV77YPmCXpG89N3Hvi5nvxJ0jM4WHAFSmY";

    [Test]
    [Arguments("/flywheel")]
    [Arguments("/")]
    [Arguments("/operations/engine")]
    public async Task EnforcesOriginsAndPreflight(string enginePath)
    {
        string prefix = enginePath.TrimEnd('/');
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlywheel().AddDashboard(options =>
        {
            options.PasswordPhc = PasswordPhc;
            options.EnginePath = enginePath;
            options.AllowedOrigins = ["https://localhost:7004"];
        });
        await using ServiceProvider provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseFlywheelDashboard();
        app.Run(context => { context.Response.StatusCode = 204; return Task.CompletedTask; });
        RequestDelegate pipeline = app.Build();

        async Task<DefaultHttpContext> Send(string? origin, string? path = null, bool preflight = false, bool websocket = false)
        {
            var context = new DefaultHttpContext { RequestServices = provider };
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("localhost", 7002);
            context.Request.Path = path ?? $"{prefix}/jobs";
            context.Request.Method = preflight ? "OPTIONS" : "GET";
            if (origin is not null) context.Request.Headers.Origin = origin;
            if (preflight)
            {
                context.Request.Headers.AccessControlRequestMethod = "POST";
                context.Request.Headers.AccessControlRequestHeaders = "Content-Type,X-Flywheel-CSRF";
            }
            if (websocket)
            {
                context.Request.Headers.Upgrade = "websocket";
                context.Request.Headers.Connection = "Upgrade";
            }
            await pipeline(context);
            return context;
        }

        foreach (string origin in new[] { "https://evil.example", "https://localhost:7005", "http://localhost:7004", "null", "https://localhost:7004.evil.example", "https://localhost:7004/path" })
        {
            if ((await Send(origin)).Response.StatusCode != 403 ||
                (await Send(origin, $"{prefix}/hub", websocket: true)).Response.StatusCode != 403)
                throw new Exception($"Untrusted origin was allowed: {origin}");
        }
        foreach (string? origin in new[] { null, "https://localhost:7002", "https://localhost:7004" })
            if ((await Send(origin)).Response.StatusCode != 204)
                throw new Exception($"Expected origin rejected: {origin}");
        if ((await Send("https://localhost:7004", $"{prefix}/hub", websocket: true)).Response.StatusCode != 204)
            throw new Exception("Allowed WebSocket handshake rejected.");
        DefaultHttpContext preflight = await Send("https://localhost:7004", preflight: true);
        if (preflight.Response.StatusCode != 204 || preflight.Response.Headers.AccessControlAllowOrigin != "https://localhost:7004" ||
            preflight.Response.Headers.AccessControlAllowCredentials != "true")
            throw new Exception("Credentialed preflight was not allowed.");
        if ((await Send("https://evil.example", "/health")).Response.StatusCode != 204)
            throw new Exception("Dashboard policy affected an unrelated endpoint.");
    }

    [Test]
    public void RejectsInvalidConfiguration()
    {
        foreach (string origin in new[] { "*", "null", "https://*.example.com", "https://example.com/path", "https://user@example.com", "https://example.com?query=1", "https://example.com#fragment" })
        {
            try
            {
                new ServiceCollection().AddFlywheel().AddDashboard(options =>
                {
                    options.PasswordPhc = PasswordPhc;
                    options.AllowedOrigins = [origin];
                });
            }
            catch (InvalidOperationException) { continue; }
            throw new Exception($"Invalid allowed origin accepted: {origin}");
        }
    }
}
