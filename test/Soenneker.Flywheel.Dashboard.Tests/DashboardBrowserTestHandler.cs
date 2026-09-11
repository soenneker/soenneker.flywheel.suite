using System.Net;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class DashboardBrowserTestHandler(HttpMessageHandler inner, CookieContainer? cookies = null) : DelegatingHandler(inner)
{
    private readonly CookieContainer _cookies = cookies ?? new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"), out IDictionary<string, object>? options) ||
            !options.TryGetValue("credentials", out object? credentials) || !Equals(credentials, "include"))
            throw new InvalidOperationException("Flywheel request omitted browser credentials.");
        string cookies = _cookies.GetCookieHeader(request.RequestUri!);
        if (cookies.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookies);
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
            foreach (string value in values) _cookies.SetCookies(request.RequestUri!, value);
        return response;
    }
}
