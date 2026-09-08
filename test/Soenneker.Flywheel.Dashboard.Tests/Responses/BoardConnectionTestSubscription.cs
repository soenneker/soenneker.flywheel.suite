using Soenneker.Flywheel.Dashboard.Communication.Abstract;

namespace Soenneker.Flywheel.Dashboard.Tests;

internal sealed class BoardConnectionTestSubscription(Func<Task> restored) : IFlywheelLiveSubscription
{
    public bool IsConnected { get; set; }
    public bool Disposed { get; private set; }
    public int Subscriptions { get; private set; }
    public int Version { get; private set; }
    public string Query { get; private set; } = "";

    public async ValueTask Start(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        await restored();
    }

    public Task SubscribeBoard(int version, string query, int offset, int count, DateTimeOffset? startAt,
        DateTimeOffset? endAt, CancellationToken cancellationToken, string? excludedStates = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Subscriptions++;
        Version = version;
        Query = query;
        return Task.CompletedTask;
    }

    public Task SubscribeJob(int version, string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SubscribeLogs(int version, string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
