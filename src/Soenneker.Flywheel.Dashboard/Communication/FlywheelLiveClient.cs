using Soenneker.Flywheel.Communication.Abstract;
using Microsoft.AspNetCore.SignalR.Client;
using Soenneker.Flywheel.Dashboard.Communication.Abstract;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.SignalR.Web.Client;
using Soenneker.SignalR.Web.Client.Options;
using Soenneker.SignalR.Web.Clients.Abstract;

namespace Soenneker.Flywheel.Dashboard.Communication;

public sealed class FlywheelLiveClient(IFlywheelApiClient api, ISignalRWebClients clients, DashboardNavigationOptions dashboard) : IFlywheelLiveClient
{
    public ValueTask<IFlywheelLiveSubscription> Board(string id, Func<LiveBoard, Task> snapshot, Func<Task> restored, Func<Task> disconnected, CancellationToken cancellationToken = default) => Create(id, nameof(IFlywheelDashboardClient.BoardSnapshot), snapshot, restored, disconnected, cancellationToken);
    public ValueTask<IFlywheelLiveSubscription> Job(string id, Func<LiveJob, Task> snapshot, Func<Task> restored, Func<Task> disconnected, CancellationToken cancellationToken = default) => Create(id, nameof(IFlywheelDashboardClient.JobSnapshot), snapshot, restored, disconnected, cancellationToken);
    public ValueTask<IFlywheelLiveSubscription> Logs(string id, Func<LiveLogs, Task> snapshot, Func<Task> restored, Func<Task> disconnected, CancellationToken cancellationToken = default) => Create(id, nameof(IFlywheelDashboardClient.LogSnapshot), snapshot, restored, disconnected, cancellationToken);

    private async ValueTask<IFlywheelLiveSubscription> Create<T>(string id, string message, Func<T, Task> snapshot, Func<Task> restored, Func<Task> disconnected, CancellationToken cancellationToken)
    {
        SignalRWebClient client = await clients.Get(id, new SignalRWebClientOptions
        {
            HubUrl = new Uri(api.BaseAddress, dashboard.EngineEndpoint("hub")).AbsoluteUri,
            HttpMessageHandlerFactory = inner => new DashboardCredentialsHandler(inner),
            ConnectionRestored = _ => restored(),
            Log = false
        }, cancellationToken);
        IDisposable subscription = client.Connection.On(message, snapshot);
        Func<Exception?, Task> onDisconnected = _ => disconnected();
        client.Connection.Closed += onDisconnected;
        client.Connection.Reconnecting += onDisconnected;
        return new FlywheelLiveSubscription(id, client, clients, subscription, onDisconnected, cancellationToken);
    }
}
