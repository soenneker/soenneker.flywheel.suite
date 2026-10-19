using Soenneker.Flywheel.Dashboard.Communication;
using System.Net;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardSessionTimeoutTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task UnauthorizedRequestsExpireOnlyAnAuthenticatedSession(bool authenticated)
    {
        var session = new DashboardSessionState();
        session.SetAuthenticated(authenticated, "admin");
        using var http = new HttpClient(new RouterTestHttpHandler()) { BaseAddress = new Uri("https://example.test/") };
        var client = new FlywheelApiClient(http, new RouterTestNavigationManager("https://example.test/", "https://example.test/"),
            new DashboardNavigationOptions()) { Session = session };

        using HttpResponseMessage response = await client.Get("flywheel/jobs/search");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(session.IsAuthenticated).IsFalse();
        await Assert.That(session.HasTimedOut).IsEqualTo(authenticated);
        session.SetAuthenticated(false);
        await Assert.That(session.HasTimedOut).IsEqualTo(authenticated);
        session.SetAuthenticated(true, "admin");
        await Assert.That(session.HasTimedOut).IsFalse();
    }
}
