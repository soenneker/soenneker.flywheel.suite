using System.Net;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class RouterTestHttpHandler : HttpMessageHandler
{
    public List<string> Paths { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Paths.Add(request.RequestUri!.AbsolutePath);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}
