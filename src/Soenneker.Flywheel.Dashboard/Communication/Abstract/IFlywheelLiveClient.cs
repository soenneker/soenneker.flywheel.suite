using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Communication.Abstract;

/// <summary>Creates page-owned live subscriptions with common authentication, reconnection, and cleanup.</summary>
public interface IFlywheelLiveClient
{
    /// <summary>Creates a board subscription. Call Start after assigning the returned subscription.</summary>
    ValueTask<IFlywheelLiveSubscription> Board(string id, Func<LiveBoard, Task> snapshot, Func<Task> restored, Func<Task> disconnected, CancellationToken cancellationToken = default);
    /// <summary>Creates an execution subscription.</summary>
    ValueTask<IFlywheelLiveSubscription> Job(string id, Func<LiveJob, Task> snapshot, Func<Task> restored, Func<Task> disconnected, CancellationToken cancellationToken = default);
    /// <summary>Creates an execution log subscription.</summary>
    ValueTask<IFlywheelLiveSubscription> Logs(string id, Func<LiveLogs, Task> snapshot, Func<Task> restored, Func<Task> disconnected, CancellationToken cancellationToken = default);
}
