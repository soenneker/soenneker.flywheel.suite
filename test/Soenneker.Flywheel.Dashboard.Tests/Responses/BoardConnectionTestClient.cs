using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Dashboard.Communication.Abstract;

namespace Soenneker.Flywheel.Dashboard.Tests;

// A transport boundary for exercising shell/page lifetime without a browser socket.
internal sealed class BoardConnectionTestClient : IFlywheelLiveClient
{
    public int Connections { get; private set; }
    public BoardConnectionTestSubscription Transport { get; private set; } = null!;
    public Func<LiveBoard, Task> Snapshot { get; private set; } = null!;
    public Func<Task> Restored { get; private set; } = null!;
    public Func<Task> Disconnected { get; private set; } = null!;
    public CancellationToken Lifetime { get; private set; }

    public ValueTask<IFlywheelLiveSubscription> Board(string id, Func<LiveBoard, Task> snapshot,
        Func<Task> restored, Func<Task> disconnected, CancellationToken cancellationToken = default)
    {
        Connections++;
        Snapshot = snapshot;
        Restored = restored;
        Disconnected = disconnected;
        Lifetime = cancellationToken;
        Transport = new BoardConnectionTestSubscription(restored);
        return ValueTask.FromResult<IFlywheelLiveSubscription>(Transport);
    }

    public ValueTask<IFlywheelLiveSubscription> Job(string id, Func<LiveJob, Task> snapshot,
        Func<Task> restored, Func<Task> disconnected, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask<IFlywheelLiveSubscription> Logs(string id, Func<LiveLogs, Task> snapshot,
        Func<Task> restored, Func<Task> disconnected, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
