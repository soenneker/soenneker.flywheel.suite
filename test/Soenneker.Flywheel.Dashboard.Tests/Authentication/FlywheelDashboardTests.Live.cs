using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Soenneker.Hashing.Pbkdf2;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Soenneker.Flywheel.Core.Dashboard;
using Soenneker.Flywheel.Core.Dtos;
using Soenneker.Flywheel.Core.Registrars;
using Soenneker.Flywheel.Core.Stores.Abstract;
using System.Net.Http.Json;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    [Test]
    public async Task SignalRPushesSnapshotsOnlyOnChangesAndResubscribes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddFlywheel().AddDashboard(o => o.PasswordPhc = Pbkdf2HashingUtil.Hash("live-password"));
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.AddHostedService<DashboardNotifications>();
        var store = new SearchStore();
        builder.Services.AddSingleton<IJobStore>(store);
        builder.Services.AddSingleton<IJobLogStore>(store);
        await using var app = builder.Build();
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers();
        app.MapFlywheelDashboard();
        await app.StartAsync();
        using var http = app.GetTestClient();
        http.BaseAddress = new Uri("https://localhost");
        var csrfResponse = await http.GetAsync("/flywheel/csrf");
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<Csrf>();
        string csrfCookie = csrfResponse.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        http.DefaultRequestHeaders.Add("Cookie", csrfCookie);
        http.DefaultRequestHeaders.Add("X-Flywheel-CSRF", csrf!.Token);
        var login = await http.PostAsJsonAsync("/flywheel/login", new { Username = "admin", Password = "live-password" });
        string cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        await using var connection = new HubConnectionBuilder().WithUrl("https://localhost/flywheel/hub", options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
            options.Headers["Cookie"] = cookie;
            options.WebSocketFactory = async (context, ct) =>
            {
                var client = app.GetTestServer().CreateWebSocketClient();
                client.ConfigureRequest = request => request.Headers.Cookie = cookie;
                return await client.ConnectAsync(context.Uri, ct);
            };
        }).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var boards = Channel.CreateUnbounded<JsonElement>();
        var logs = Channel.CreateUnbounded<JsonElement>();
        using var boardHandler = connection.On<JsonElement>("BoardSnapshot", value => boards.Writer.TryWrite(value));
        using var logHandler = connection.On<JsonElement>("LogSnapshot", value => logs.Writer.TryWrite(value));
        await store.Subscribed.Task.WaitAsync(timeout.Token);
        await connection.StartAsync(timeout.Token);
        await connection.InvokeAsync("SubscribeBoard", 1, "invoice", 0, 25, true, null, null, timeout.Token);
        var first = await boards.Reader.ReadAsync(timeout.Token);
        Check(first.GetProperty("version").GetInt32() == 1 && first.GetProperty("totalCount").GetInt32() == 51, "Initial snapshot missing");
        Check(!first.ToString().Contains("private-payload") && !first.ToString().Contains("private-token"), "Push leaked private state");
        int calls = store.Calls;
        await Task.Delay(2200, timeout.Token);
        Check(!boards.Reader.TryRead(out _) && store.Calls == calls, "Idle dashboard still polls or broadcasts");
        store.Changes.Writer.TryWrite(new JobChange("Logs", "one"));
        await Task.Delay(100, timeout.Token);
        Check(store.Calls == calls, "Log change unnecessarily refreshed the board");
        store.Changes.Writer.TryWrite(new JobChange("Job", "one"));
        Check((await boards.Reader.ReadAsync(timeout.Token)).GetProperty("version").GetInt32() == 1, "Committed change was not pushed");
        await connection.InvokeAsync("SubscribeBoard", 2, "new-query", 50, 10, false, null, null, timeout.Token);
        Check((await boards.Reader.ReadAsync(timeout.Token)).GetProperty("version").GetInt32() == 2 && store.Query == "new-query" && store.Offset == 50, "Search subscription was not replaced");
        await connection.InvokeAsync("SubscribeLogs", 3, "one", timeout.Token);
        Check((await logs.Reader.ReadAsync(timeout.Token)).GetProperty("entries").GetArrayLength() == 1, "Initial logs missing");
        store.Changes.Writer.TryWrite(new JobChange("Logs", "one"));
        await logs.Reader.ReadAsync(timeout.Token);
        await connection.StopAsync(timeout.Token);
        store.Changes.Writer.TryWrite(new JobChange("Job", "one"));
        await connection.StartAsync(timeout.Token);
        await connection.InvokeAsync("SubscribeBoard", 4, "reconnected", 0, 50, true, null, null, timeout.Token);
        Check((await boards.Reader.ReadAsync(timeout.Token)).GetProperty("version").GetInt32() == 4, "Reconnect did not obtain a fresh snapshot");
        await connection.StopAsync(timeout.Token);
        await app.StopAsync(timeout.Token);
    }
}
