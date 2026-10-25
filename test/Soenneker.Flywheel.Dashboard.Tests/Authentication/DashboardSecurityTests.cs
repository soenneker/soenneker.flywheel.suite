using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Soenneker.Flywheel.Core.Options;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Memory;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Hashing.Pbkdf2;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardSecurityTests
{
    [Test]
    public async Task Individual_permissions_and_credential_rotation_are_enforced(CancellationToken cancellationToken)
    {
        string password = Guid.NewGuid().ToString();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<System.Text.Json.Serialization.JsonSerializerContext>(TestJsonContext.Default);
        builder.Services.AddFlywheel().AddMemory().AddDashboard(options =>
        {
            options.Accounts["reader"] = new() { PasswordPhc = Pbkdf2HashingUtil.Hash(password) };
            options.Accounts["operator"] = new() { PasswordPhc = Pbkdf2HashingUtil.Hash(password), CanManage = true };
        });
        builder.Services.RemoveAll<IHostedService>();
        await using var app = builder.Build();
        app.UseRouting(); app.UseFlywheelDashboard(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
        app.MapFlywheelDashboard();
        await app.StartAsync(cancellationToken);
        var store = app.Services.GetRequiredService<MemoryJobStore>();
        string id = await store.Enqueue(new("job", "{}", new(), TimeSpan.Zero), cancellationToken);
        var options = app.Services.GetRequiredService<DashboardOptions>();
        var sessions = new Dictionary<string, CookieContainer>();
        using var reader = await Login("reader");
        if (!(await reader.GetAsync("/flywheel/jobs", cancellationToken)).IsSuccessStatusCode ||
            (await reader.PostAsync($"/flywheel/jobs/{id}/cancel", null, cancellationToken)).StatusCode != HttpStatusCode.Forbidden)
            throw new Exception("Read-only authorization failed.");
        using var manager = await Login("operator");
        if (!(await manager.PostAsync($"/flywheel/jobs/{id}/cancel", null, cancellationToken)).IsSuccessStatusCode)
            throw new Exception("An operator could not manage jobs.");
        string cookie = sessions["operator"].GetCookieHeader(new Uri("https://localhost/"));
        await using var connection = new HubConnectionBuilder().WithUrl("https://localhost/flywheel/hub", hub =>
        {
            hub.Transports = HttpTransportType.WebSockets;
            hub.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
            hub.Headers["Cookie"] = cookie;
            hub.WebSocketFactory = async (context, token) =>
            {
                var socket = app.GetTestServer().CreateWebSocketClient();
                socket.ConfigureRequest = request => request.Headers.Cookie = cookie;
                return await socket.ConnectAsync(context.Uri, token);
            };
        }).Build();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ => { disconnected.TrySetResult(); return Task.CompletedTask; };
        await connection.StartAsync(cancellationToken);
        options.Accounts["operator"] = options.Accounts["operator"] with { PasswordPhc = Pbkdf2HashingUtil.Hash(Guid.NewGuid().ToString()) };
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if ((await manager.GetAsync("/flywheel/user", cancellationToken)).StatusCode != HttpStatusCode.Unauthorized)
            throw new Exception("Credential rotation left an old session authorized.");
        options.Accounts.TryRemove("reader", out _);
        if ((await reader.GetAsync("/flywheel/user", cancellationToken)).StatusCode != HttpStatusCode.Unauthorized)
            throw new Exception("Removing an account left its session authorized.");

        async Task<HttpClient> Login(string username)
        {
            var cookies = new CookieContainer();
            sessions[username] = cookies;
            var client = new HttpClient(new DashboardBrowserTestHandler(app.GetTestServer().CreateHandler(), cookies, requireBrowserOptions: false))
                { BaseAddress = new Uri("https://localhost/") };
            using var csrfResponse = await client.GetAsync("/flywheel/csrf", cancellationToken);
            var csrf = (await csrfResponse.Content.ReadFromJsonAsync<Csrf>(cancellationToken))!;
            client.DefaultRequestHeaders.Add("X-Flywheel-CSRF", csrf.Token);
            using var login = await client.PostAsJsonAsync("/flywheel/login", new { Username = username, Password = password }, cancellationToken);
            if (!login.IsSuccessStatusCode) { client.Dispose(); throw new Exception("Individual account login failed."); }
            client.DefaultRequestHeaders.Remove("X-Flywheel-CSRF");
            using var authenticatedCsrf = await client.GetAsync("/flywheel/csrf", cancellationToken);
            csrf = (await authenticatedCsrf.Content.ReadFromJsonAsync<Csrf>(cancellationToken))!;
            client.DefaultRequestHeaders.Add("X-Flywheel-CSRF", csrf.Token);
            return client;
        }
    }
}
