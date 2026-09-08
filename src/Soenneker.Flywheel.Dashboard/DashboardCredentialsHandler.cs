using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Soenneker.Flywheel.Dashboard;

internal sealed class DashboardCredentialsHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return base.SendAsync(request, cancellationToken);
    }
}
