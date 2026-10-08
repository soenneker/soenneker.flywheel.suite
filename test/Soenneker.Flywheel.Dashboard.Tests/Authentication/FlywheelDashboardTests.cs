using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Soenneker.Hashing.Pbkdf2;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Threading;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    [Test]
    [Arguments("/flywheel")]
    [Arguments("/")]
    [Arguments("/operations/engine")]
    public async ValueTask CookieAuthenticationCsrfAndHubProtection(string enginePath, CancellationToken cancellationToken)
    {
        string prefix = enginePath.TrimEnd('/');
        var password = Guid.NewGuid().ToString("N");
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<System.Text.Json.Serialization.JsonSerializerContext>(TestJsonContext.Default);
        builder.Services.AddFlywheel().AddDashboard(o => { o.EnginePath = enginePath; o.PasswordPhc = Pbkdf2HashingUtil.Hash(password); o.AllowedOrigins = ["https://localhost:7039"]; });
        var store = new SearchStore();
        builder.Services.RemoveAll<IHostedService>(); builder.Services.AddSingleton<IJobStore>(store);
        builder.Services.AddSingleton<IJobLogStore>(store);
        builder.Services.AddSingleton<INodeStore>(store);
        await using WebApplication app = builder.Build();
        app.UseRouting(); app.UseFlywheelDashboard(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
        app.MapFlywheelDashboard();
        await app.StartAsync(cancellationToken: cancellationToken);
        using HttpClient client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost:7039");
        using HttpResponseMessage anonymousUser = await client.GetAsync($"{prefix}/user", cancellationToken: cancellationToken);
        Check(anonymousUser.StatusCode == HttpStatusCode.Unauthorized, "User identity must require authentication");
        Check(anonymousUser.Headers.TryGetValues("Access-Control-Allow-Origin", out var anonymousOrigins) && anonymousOrigins.Single() == "https://localhost:7039",
            "Signed-out dashboard must receive CORS headers to recognize the 401");
        Check((await client.GetAsync($"{prefix}/jobs", cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.Unauthorized, "Jobs unprotected");
        Check((await client.GetAsync($"{prefix}/jobs/search?q=test", cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.Unauthorized, "Search unprotected");
        Check(store.Calls == 0, "Unauthenticated search reached storage");
        Check((await client.GetAsync($"{prefix}/jobs/one/logs", cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.Unauthorized, "Logs unprotected");
        Check((await client.PostAsync($"{prefix}/hub/negotiate?negotiateVersion=1", null, cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.Unauthorized, "Hub unprotected");
        Check((await client.PostAsJsonAsync($"{prefix}/login", new { Username = "admin", Password = password }, cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.BadRequest, "Missing CSRF accepted");
        HttpResponseMessage csrfResponse = await client.GetAsync($"{prefix}/csrf", cancellationToken: cancellationToken);
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<Csrf>(cancellationToken: cancellationToken);
        string csrfCookie = csrfResponse.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", csrfCookie);
        client.DefaultRequestHeaders.Add("X-Flywheel-CSRF", csrf!.Token);
        Check((await client.PostAsJsonAsync($"{prefix}/login", new { Username = "admin", Password = "wrong" }, cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.Unauthorized, "Wrong password accepted");
        HttpResponseMessage login = await client.PostAsJsonAsync($"{prefix}/login", new { Username = "admin", Password = password }, cancellationToken: cancellationToken);
        Check(login.StatusCode == HttpStatusCode.NoContent, "Login failed: " + await login.Content.ReadAsStringAsync(cancellationToken: cancellationToken));
        string cookie = login.Headers.GetValues("Set-Cookie").Single();
        Check(cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase), "Cookie flags missing");
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", csrfCookie + "; " + cookie.Split(';')[0]);
        using HttpResponseMessage user = await client.GetAsync($"{prefix}/user", cancellationToken: cancellationToken);
        Check(user.IsSuccessStatusCode && (await user.Content.ReadFromJsonAsync<Soenneker.Flywheel.Communication.Responses.DashboardUser>(cancellationToken: cancellationToken))?.Username == "admin", "Current user unavailable");
        Check(user.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins) && origins.Single() == "https://localhost:7039" &&
            user.Headers.TryGetValues("Access-Control-Allow-Credentials", out var credentials) && credentials.Single() == "true", "Current user missing credentialed CORS headers");
        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, $"{prefix}/user");
        deniedRequest.Headers.Add("Origin", "https://untrusted.example");
        using HttpResponseMessage deniedUser = await client.SendAsync(deniedRequest, cancellationToken: cancellationToken);
        Check(deniedUser.StatusCode == HttpStatusCode.Forbidden && !deniedUser.Headers.Contains("Access-Control-Allow-Origin"), "Untrusted origin accepted for current user");
        Check((await client.PostAsync($"{prefix}/hub/negotiate?negotiateVersion=1", null, cancellationToken: cancellationToken)).IsSuccessStatusCode, "Authenticated hub rejected");
        HttpResponseMessage search = await client.GetAsync($"{prefix}/jobs/search?q=invoice%26monthly&offset=50&count=25", cancellationToken: cancellationToken);
        string json = await search.Content.ReadAsStringAsync(cancellationToken: cancellationToken);
        Check(search.Headers.CacheControl?.NoStore == true, "Dashboard response may be cached");
        Check(search.IsSuccessStatusCode && store.Query == "invoice&monthly" && store.Offset == 50 && store.Count == 25, "Search arguments were not forwarded");
        Check(json.Contains("totalCount") && !json.Contains("private-payload") && !json.Contains("private-token"), "Search leaked private data");
        using HttpResponseMessage filteredSearch = await client.GetAsync($"{prefix}/jobs/search?q=invoice&excludedStates=Queued", cancellationToken: cancellationToken);
        using JsonDocument filteredJson = System.Text.Json.JsonDocument.Parse(await filteredSearch.Content.ReadAsStringAsync(cancellationToken: cancellationToken));
        Check(filteredSearch.IsSuccessStatusCode && filteredJson.RootElement.GetProperty("totalCount").GetInt32() == 0 &&
            filteredJson.RootElement.GetProperty("items").GetArrayLength() == 0, "HTTP filtering left hidden statuses in the table");
        Check((await client.GetAsync($"{prefix}/jobs/search?excludedStates=invalid", cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.BadRequest,
            "Invalid status filter accepted");
        using HttpResponseMessage detail = await client.GetAsync($"{prefix}/jobs/one", cancellationToken: cancellationToken);
        string detailJson = await detail.Content.ReadAsStringAsync(cancellationToken: cancellationToken);
        Check(detail.IsSuccessStatusCode && detailJson.Contains("\"state\":\"Queued\""), "Job detail did not return a named state");
        Check(!detailJson.Contains("\"payload\"") && !detailJson.Contains("\"token\""), "Job detail leaked execution capabilities");
        var projection = System.Text.Json.JsonSerializer.Deserialize<Soenneker.Flywheel.Communication.Responses.JobView>(detailJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Check(projection is { MaxAttempts: 1, TimeoutSeconds: 300, Priority: "Normal" }, "Execution policy is missing from the public detail projection");
        Check((await client.GetAsync($"{prefix}/jobs/missing", cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.NotFound, "Missing job detail should return 404");
        Check((await client.GetAsync($"{prefix}/jobs/search?q=" + new string('x', 201), cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.BadRequest, "Oversized query accepted");
        Check((await client.GetAsync($"{prefix}/jobs/search?offset=invalid", cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.BadRequest, "Malformed offset accepted");
        Check((await client.GetAsync($"{prefix}/jobs/history?startAt=invalid", cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.BadRequest, "Malformed date accepted");
        Check((await client.GetAsync($"{prefix}/jobs/search?offset=-1", cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.BadRequest, "Invalid offset accepted");
        Check((await client.GetAsync($"{prefix}/jobs/search?count=201", cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.BadRequest, "Invalid page size accepted");
        Check((await client.GetAsync($"{prefix}/jobs/one/logs", cancellationToken: cancellationToken)).IsSuccessStatusCode, "Authenticated logs unavailable");
        Check((await client.GetAsync($"{prefix}/jobs/missing/logs", cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.NotFound, "Missing job logs should return 404");
        Check((await client.GetAsync($"{prefix}/jobs/one/logs?count=201", cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.BadRequest, "Unbounded logs accepted");
        var authenticatedCsrf = await client.GetFromJsonAsync<Csrf>($"{prefix}/csrf", cancellationToken: cancellationToken);
        client.DefaultRequestHeaders.Remove("X-Flywheel-CSRF");
        Check((await client.PostAsync($"{prefix}/jobs/one/cancel", null, cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.BadRequest, "Cancellation accepted without CSRF");
        Check((await client.PostAsJsonAsync($"{prefix}/logout", new { }, cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.BadRequest, "Logout accepted without CSRF");
        client.DefaultRequestHeaders.Add("X-Flywheel-CSRF", authenticatedCsrf!.Token);
        Check((await client.PostAsJsonAsync($"{prefix}/logout", new { }, cancellationToken: cancellationToken)).StatusCode == HttpStatusCode.NoContent, "Logout failed");
        await app.StopAsync(cancellationToken: cancellationToken);
    }
    [Test]
    public void MissingCredentialsFailClosed()
    {
        try { new ServiceCollection().AddFlywheel().AddDashboard(_ => { }); throw new Exception("Missing credentials allowed"); }
        catch (InvalidOperationException) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
