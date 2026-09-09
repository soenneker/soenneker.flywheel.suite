using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Soenneker.Hashing.Pbkdf2;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    [Test]
    public async Task CookieAuthenticationCsrfAndHubProtection()
    {
        var password = Guid.NewGuid().ToString("N");
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Host.UseDefaultServiceProvider(options => { options.ValidateOnBuild = true; options.ValidateScopes = true; });
        builder.WebHost.UseTestServer();
        builder.Services.AddFlywheel().AddDashboard(o => o.PasswordPhc = Pbkdf2HashingUtil.Hash(password));
        var store = new SearchStore();
        builder.Services.RemoveAll<IHostedService>(); builder.Services.AddSingleton<IJobStore>(store);
        builder.Services.AddSingleton<IJobLogStore>(store);
        builder.Services.AddSingleton<INodeStore>(store);
        await using WebApplication app = builder.Build();
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers();
        app.MapFlywheelDashboard();
        await app.StartAsync();
        using HttpClient client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        Check((await client.GetAsync("/flywheel/jobs")).StatusCode == HttpStatusCode.Unauthorized, "Jobs unprotected");
        Check((await client.GetAsync("/flywheel/jobs/search?q=test")).StatusCode == HttpStatusCode.Unauthorized, "Search unprotected");
        Check(store.Calls == 0, "Unauthenticated search reached storage");
        Check((await client.GetAsync("/flywheel/jobs/one/logs")).StatusCode == HttpStatusCode.Unauthorized, "Logs unprotected");
        Check((await client.PostAsync("/flywheel/hub/negotiate?negotiateVersion=1", null)).StatusCode == HttpStatusCode.Unauthorized, "Hub unprotected");
        Check((await client.PostAsJsonAsync("/flywheel/login", new { Username = "admin", Password = password })).StatusCode == HttpStatusCode.BadRequest, "Missing CSRF accepted");
        HttpResponseMessage csrfResponse = await client.GetAsync("/flywheel/csrf");
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<Csrf>();
        string csrfCookie = csrfResponse.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", csrfCookie);
        client.DefaultRequestHeaders.Add("X-Flywheel-CSRF", csrf!.Token);
        Check((await client.PostAsJsonAsync("/flywheel/login", new { Username = "admin", Password = "wrong" })).StatusCode == HttpStatusCode.Unauthorized, "Wrong password accepted");
        HttpResponseMessage login = await client.PostAsJsonAsync("/flywheel/login", new { Username = "admin", Password = password });
        Check(login.StatusCode == HttpStatusCode.NoContent, "Login failed: " + await login.Content.ReadAsStringAsync());
        string cookie = login.Headers.GetValues("Set-Cookie").Single();
        Check(cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase), "Cookie flags missing");
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", csrfCookie + "; " + cookie.Split(';')[0]);
        Check((await client.PostAsync("/flywheel/hub/negotiate?negotiateVersion=1", null)).IsSuccessStatusCode, "Authenticated hub rejected");
        HttpResponseMessage search = await client.GetAsync("/flywheel/jobs/search?q=invoice%26monthly&offset=50&count=25");
        string json = await search.Content.ReadAsStringAsync();
        Check(search.Headers.CacheControl?.NoStore == true, "Dashboard response may be cached");
        Check(search.IsSuccessStatusCode && store.Query == "invoice&monthly" && store.Offset == 50 && store.Count == 25, "Search arguments were not forwarded");
        Check(json.Contains("totalCount") && !json.Contains("private-payload") && !json.Contains("private-token"), "Search leaked private data");
        using HttpResponseMessage filteredSearch = await client.GetAsync("/flywheel/jobs/search?q=invoice&excludedStates=Queued");
        using var filteredJson = System.Text.Json.JsonDocument.Parse(await filteredSearch.Content.ReadAsStringAsync());
        Check(filteredSearch.IsSuccessStatusCode && filteredJson.RootElement.GetProperty("totalCount").GetInt32() == 0 &&
            filteredJson.RootElement.GetProperty("items").GetArrayLength() == 0, "HTTP filtering left hidden statuses in the table");
        Check((await client.GetAsync("/flywheel/jobs/search?excludedStates=invalid")).StatusCode == HttpStatusCode.BadRequest,
            "Invalid status filter accepted");
        using HttpResponseMessage detail = await client.GetAsync("/flywheel/jobs/one");
        string detailJson = await detail.Content.ReadAsStringAsync();
        Check(detail.IsSuccessStatusCode && detailJson.Contains("\"state\":\"Queued\""), "Job detail did not return a named state");
        Check(!detailJson.Contains("\"payload\"") && !detailJson.Contains("\"token\""), "Job detail leaked execution capabilities");
        var projection = System.Text.Json.JsonSerializer.Deserialize<Soenneker.Flywheel.Communication.Responses.JobView>(detailJson,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Check(projection is { MaxAttempts: 5, TimeoutSeconds: 300, Priority: "Normal" }, "Execution policy is missing from the public detail projection");
        Check((await client.GetAsync("/flywheel/jobs/missing")).StatusCode == HttpStatusCode.NotFound, "Missing job detail should return 404");
        Check((await client.GetAsync("/flywheel/jobs/search?q=" + new string('x', 201))).StatusCode == HttpStatusCode.BadRequest, "Oversized query accepted");
        Check((await client.GetAsync("/flywheel/jobs/search?offset=-1")).StatusCode == HttpStatusCode.BadRequest, "Invalid offset accepted");
        Check((await client.GetAsync("/flywheel/jobs/search?count=201")).StatusCode == HttpStatusCode.BadRequest, "Invalid page size accepted");
        Check((await client.GetAsync("/flywheel/jobs/one/logs")).IsSuccessStatusCode, "Authenticated logs unavailable");
        Check((await client.GetAsync("/flywheel/jobs/missing/logs")).StatusCode == HttpStatusCode.NotFound, "Missing job logs should return 404");
        Check((await client.GetAsync("/flywheel/jobs/one/logs?count=201")).StatusCode == HttpStatusCode.BadRequest, "Unbounded logs accepted");
        var authenticatedCsrf = await client.GetFromJsonAsync<Csrf>("/flywheel/csrf");
        client.DefaultRequestHeaders.Remove("X-Flywheel-CSRF");
        Check((await client.PostAsync("/flywheel/jobs/one/cancel", null)).StatusCode == HttpStatusCode.BadRequest, "Cancellation accepted without CSRF");
        Check((await client.PostAsJsonAsync("/flywheel/logout", new { })).StatusCode == HttpStatusCode.BadRequest, "Logout accepted without CSRF");
        client.DefaultRequestHeaders.Add("X-Flywheel-CSRF", authenticatedCsrf!.Token);
        Check((await client.PostAsJsonAsync("/flywheel/logout", new { })).StatusCode == HttpStatusCode.NoContent, "Logout failed");
        await app.StopAsync();
    }
    [Test]
    public void MissingCredentialsFailClosed()
    {
        try { new ServiceCollection().AddFlywheel().AddDashboard(_ => { }); throw new Exception("Missing credentials allowed"); }
        catch (InvalidOperationException) { }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
