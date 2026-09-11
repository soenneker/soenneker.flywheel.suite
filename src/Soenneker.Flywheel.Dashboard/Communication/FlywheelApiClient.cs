using System.Text;
using Soenneker.Utils.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Soenneker.Blazor.ApiClient.Dtos;
using Soenneker.Flywheel.Dashboard.Communication.Abstract;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Communication;

public sealed class FlywheelApiClient(HttpClient http, NavigationManager navigation) : IFlywheelApiClient
{
    public Uri BaseAddress { get; private set; } = http.BaseAddress ?? new Uri(navigation.BaseUri);

    public void Initialize(string baseAddress, bool requestResponseLogging)
    {
        var address = new Uri(baseAddress, UriKind.Absolute);
        if (address.Scheme != Uri.UriSchemeHttps && !(address.IsLoopback && address.Scheme == Uri.UriSchemeHttp))
            throw new ArgumentException("Flywheel requires HTTPS except on loopback.", nameof(baseAddress));
        BaseAddress = new Uri(address.AbsoluteUri.TrimEnd('/') + "/");
    }

    public ValueTask<HttpClient> GetClient(bool? allowAnonymous = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(http);
    }

    public ValueTask<string> GetAccessToken(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<string>(new NotSupportedException("Flywheel authenticates with an HttpOnly cookie, not a bearer token."));

    public ValueTask<HttpResponseMessage> Get(string uri, bool? allowAnonymous = false, CancellationToken cancellationToken = default) => Send(HttpMethod.Get, uri, null, cancellationToken);
    public ValueTask<HttpResponseMessage> Get(RequestOptions options, CancellationToken cancellationToken = default) => Get(options.Uri, options.AllowAnonymous, cancellationToken);
    public ValueTask<HttpResponseMessage> Post(string uri, object? obj, bool logResponse = true, bool? allowAnonymous = false, CancellationToken cancellationToken = default) => Send(HttpMethod.Post, uri, obj, cancellationToken);
    public ValueTask<HttpResponseMessage> Post(RequestOptions options, CancellationToken cancellationToken = default) => Post(options.Uri, options.Object, false, options.AllowAnonymous, cancellationToken);
    public ValueTask<HttpResponseMessage> Put(string uri, object obj, bool? allowAnonymous = false, CancellationToken cancellationToken = default) => Send(HttpMethod.Put, uri, obj, cancellationToken);
    public ValueTask<HttpResponseMessage> Put(RequestOptions options, CancellationToken cancellationToken = default) => Send(HttpMethod.Put, options.Uri, options.Object, cancellationToken);
    public ValueTask<HttpResponseMessage> Delete(string uri, CancellationToken cancellationToken = default) => Send(HttpMethod.Delete, uri, null, cancellationToken);
    public ValueTask<HttpResponseMessage> Delete(RequestOptions options, CancellationToken cancellationToken = default) => Delete(options.Uri, cancellationToken);

    public async ValueTask<HttpResponseMessage> Upload(RequestUploadOptions options, CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(options.Stream), "file", options.FileName);
        if (options.Object is not null) content.Add(CreateJsonContent(options.Object), "metadata");
        return await SendContent(HttpMethod.Post, options.Uri, content, cancellationToken);
    }

    private async ValueTask<HttpResponseMessage> Send(HttpMethod method, string uri, object? body, CancellationToken cancellationToken)
    {
        using StringContent? content = body is null ? null : CreateJsonContent(body);
        return await SendContent(method, uri, content, cancellationToken);
    }

    private async ValueTask<HttpResponseMessage> SendContent(HttpMethod method, string uri, HttpContent? content, CancellationToken cancellationToken)
    {
        var address = new Uri(BaseAddress, uri);
        if (address.GetLeftPart(UriPartial.Authority) != BaseAddress.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("Flywheel requests must target the configured backend origin.");
        using var request = new HttpRequestMessage(method, address) { Content = content };
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            using HttpResponseMessage response = await Get("flywheel/csrf", cancellationToken: cancellationToken);
            response.EnsureSuccessStatusCode();
            Csrf csrf = await JsonUtil.Deserialize<Csrf>(response, cancellationToken: cancellationToken)
                        ?? throw new InvalidOperationException("Missing Flywheel antiforgery token.");
            request.Headers.Add("X-Flywheel-CSRF", csrf.Token);
        }
        return await http.SendAsync(request, cancellationToken);
    }

    private static StringContent CreateJsonContent(object value) =>
        new(JsonUtil.Serialize(value) ?? "null", Encoding.UTF8, "application/json");
}
