using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core.Dashboard;
using Soenneker.Flywheel.Core.Dashboard.Endpoints;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Core.Registrars;

namespace Soenneker.Flywheel.Core.Tests.Jobs;

public sealed class DashboardLoginSecurityTests
{
    [Test]
    public async Task Exhausted_client_cannot_block_another_client()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFlywheel().AddDashboard(options => options.PasswordPhc =
            "$pbkdf2-sha256$i=300000$QtAsVucfLlySNc4h9KewFw$X4zUX9pcEuV77YPmCXpG89N3Hvi5nvxJ0jM4WHAFSmY");
        await using ServiceProvider provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseRateLimiter();
        app.Run(context => { context.Response.StatusCode = 204; return Task.CompletedTask; });
        RequestDelegate pipeline = app.Build();
        // Use real endpoint metadata and the actual ASP.NET rate limiting middleware.
        var route = new Microsoft.AspNetCore.Routing.RouteEndpointBuilder(_ => Task.CompletedTask,
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/flywheel/login"), 0);
        route.Metadata.Add(new Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute("FlywheelLogin"));
        async Task<int> Send(string ip)
        {
            var context = new DefaultHttpContext { RequestServices = provider };
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            context.SetEndpoint(route.Build());
            await pipeline(context);
            return context.Response.StatusCode;
        }
        for (int i = 0; i < 10; i++)
            if (await Send("192.0.2.1") != 204) throw new Exception("Client was limited prematurely.");
        if (await Send("::ffff:192.0.2.1") != 429) throw new Exception("Client limit was bypassed.");
        if (await Send("192.0.2.2") != 204) throw new Exception("One client exhausted another client's allowance.");

        var limiter = provider.GetRequiredService<DashboardLoginLimiter>();
        using var first = limiter.Acquire();
        using var second = limiter.Acquire();
        using var rejected = limiter.Acquire();
        if (!first.IsAcquired || !second.IsAcquired || rejected.IsAcquired)
            throw new Exception("Password verification concurrency was not bounded.");
        var loginContext = new DefaultHttpContext { RequestServices = provider };
        DashboardOptions options = provider.GetRequiredService<DashboardOptions>();
        IResult busy = await FlywheelAuthenticationEndpoints.Login(loginContext, options, limiter, new LoginRequest("admin", "incorrect"));
        if (busy is not IStatusCodeHttpResult { StatusCode: 429 })
            throw new Exception("Login bypassed the password verification concurrency limit.");
        first.Dispose();
        using var recovered = limiter.Acquire();
        if (!recovered.IsAcquired) throw new Exception("Completed verification did not release capacity.");
        recovered.Dispose();
        second.Dispose();
        IResult invalid = await FlywheelAuthenticationEndpoints.Login(loginContext, options, limiter, new LoginRequest("admin", "incorrect"));
        if (invalid is not IStatusCodeHttpResult { StatusCode: 401 })
            throw new Exception("Login did not resume credential verification after capacity was released.");
        using var releasedFirst = limiter.Acquire();
        using var releasedSecond = limiter.Acquire();
        if (!releasedFirst.IsAcquired || !releasedSecond.IsAcquired)
            throw new Exception("Failed authentication leaked a verification permit.");
    }
}
