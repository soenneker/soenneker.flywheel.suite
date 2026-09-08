using Soenneker.Flywheel.Communication.Abstract;
using Microsoft.AspNetCore.SignalR.Client;
using Soenneker.Flywheel.Dashboard.Communication.Abstract;
using Soenneker.SignalR.Web.Client;
using Soenneker.SignalR.Web.Clients.Abstract;

namespace Soenneker.Flywheel.Dashboard.Communication;

internal sealed class FlywheelLiveSubscription(string id, SignalRWebClient client, ISignalRWebClients clients,
    IDisposable subscription, Func<Exception?, Task> disconnected, CancellationToken lifetimeToken) : IFlywheelLiveSubscription
{
    private int _disposed;
    public bool IsConnected => client.Connection.State == HubConnectionState.Connected;
    public async ValueTask Start(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, cancellationToken);
        await client.StartConnection(linked.Token);
    }
    public Task SubscribeBoard(int version, string query, int offset, int count, DateTimeOffset? startAt, DateTimeOffset? endAt, CancellationToken cancellationToken) =>
        client.Connection.InvokeAsync(nameof(IFlywheelDashboardHub.SubscribeBoard), version, query, offset, count, true, startAt, endAt, cancellationToken);
    public Task SubscribeJob(int version, string jobId, CancellationToken cancellationToken) => client.Connection.InvokeAsync(nameof(IFlywheelDashboardHub.SubscribeJob), version, jobId, cancellationToken);
    public Task SubscribeLogs(int version, string jobId, CancellationToken cancellationToken) => client.Connection.InvokeAsync(nameof(IFlywheelDashboardHub.SubscribeLogs), version, jobId, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        subscription.Dispose();
        client.Connection.Closed -= disconnected;
        client.Connection.Reconnecting -= disconnected;
        await clients.Remove(id);
    }
}
