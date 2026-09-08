using System.Net;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class DashboardBrowserTestHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly CookieContainer _cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"), out var options) ||
            !options.TryGetValue("credentials", out var credentials) || !Equals(credentials, "include"))
            throw new InvalidOperationException("Flywheel request omitted browser credentials.");
        var cookies = _cookies.GetCookieHeader(request.RequestUri!);
        if (cookies.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookies);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
            foreach (var value in values) _cookies.SetCookies(request.RequestUri!, value);
        return response;
    }
}
