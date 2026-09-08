using System.Net;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class DashboardCredentialsTestHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"), out var options) ||
            !options.TryGetValue("credentials", out var credentials) || !Equals(credentials, "include"))
            throw new InvalidOperationException("Browser request did not include authentication cookies.");

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }
}
