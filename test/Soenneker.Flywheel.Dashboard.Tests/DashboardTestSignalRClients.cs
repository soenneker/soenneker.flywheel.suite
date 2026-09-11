using Microsoft.AspNetCore.Http.Connections;
using Soenneker.SignalR.Web.Client;
using Soenneker.SignalR.Web.Client.Options;
using Soenneker.SignalR.Web.Clients;
using Soenneker.SignalR.Web.Clients.Abstract;

namespace Soenneker.Flywheel.Dashboard.Tests;

// Connects the real SignalR client to TestServer without replacing its configured URL.
internal sealed class DashboardTestSignalRClients(Func<HttpMessageHandler> handler) : ISignalRWebClients
{
    private readonly SignalRWebClients _clients = new();

    public ValueTask<SignalRWebClient> Get(string id, SignalRWebClientOptions? options = null, CancellationToken cancellationToken = default) =>
        _clients.Get(id, Configure(options), cancellationToken);

    public SignalRWebClient GetSync(string id, SignalRWebClientOptions? options = null, CancellationToken cancellationToken = default) =>
        _clients.GetSync(id, Configure(options), cancellationToken);

    private SignalRWebClientOptions Configure(SignalRWebClientOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Func<HttpMessageHandler, HttpMessageHandler>? credentials = options.HttpMessageHandlerFactory;
        options.HttpMessageHandlerFactory = inner =>
        {
            inner.Dispose();
            HttpMessageHandler transport = handler();
            return credentials?.Invoke(transport) ?? transport;
        };
        options.TransportType = HttpTransportType.LongPolling;
        options.MaxRetryAttempts = 0;
        options.ReconnectIndefinitely = false;
        return options;
    }

    public ValueTask<bool> Remove(string id) => _clients.Remove(id);
    public void RemoveSync(string id) => _clients.RemoveSync(id);
    public ValueTask DisposeAsync() => _clients.DisposeAsync();
    public void Dispose() => _clients.Dispose();
}
