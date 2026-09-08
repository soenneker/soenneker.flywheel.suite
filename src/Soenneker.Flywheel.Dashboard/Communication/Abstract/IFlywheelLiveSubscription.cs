namespace Soenneker.Flywheel.Dashboard.Communication.Abstract;

/// <summary>Owns a keyed live connection and its event handlers. Disposal releases both.</summary>
public interface IFlywheelLiveSubscription : IAsyncDisposable
{
    /// <summary>Whether live transport is currently connected.</summary>
    bool IsConnected { get; }
    /// <summary>Starts the connection and its configured recovery policy.</summary>
    ValueTask Start(CancellationToken cancellationToken = default);
    /// <summary>Replaces the board query, preserving the caller's revision.</summary>
    Task SubscribeBoard(int version, string query, int offset, int count, DateTimeOffset? startAt, DateTimeOffset? endAt, CancellationToken cancellationToken, string? excludedStates = null);
    /// <summary>Replaces the execution subscription.</summary>
    Task SubscribeJob(int version, string jobId, CancellationToken cancellationToken);
    /// <summary>Replaces the log subscription.</summary>
    Task SubscribeLogs(int version, string jobId, CancellationToken cancellationToken);
}
