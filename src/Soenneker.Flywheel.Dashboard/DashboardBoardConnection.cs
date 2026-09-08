using Soenneker.Asyncs.Semaphores;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Dashboard.Communication.Abstract;

namespace Soenneker.Flywheel.Dashboard;

// The dashboard shell and routed boards share one transport. Page cancellation must
// cancel page work, never the connection that also supplies the persistent header.
internal sealed class DashboardBoardConnection(IFlywheelLiveClient live, ActivityTotalsState totals) : IAsyncDisposable
{
    private readonly AsyncSemaphore _gate = new(1);
    private readonly AsyncSemaphore _subscribeGate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _id = $"flywheel-shell-{Guid.NewGuid():N}";
    private IFlywheelLiveSubscription? _connection;
    private int _nextVersion;
    private int _version;
    private int _subscribedVersion = -1;
    private string _query = "";
    private string? _excludedStates;
    private int _offset;
    private int _count = 1;
    private DateTimeOffset? _startAt, _endAt;
    private int _disposed;
    private Task? _activityClock;

    public bool IsConnected => _connection?.IsConnected == true;
    public DashboardLiveActivityState LiveActivity { get; private set; } = new();
    public LiveBoard? Latest { get; private set; }
    public event Func<LiveBoard, Task>? Snapshot;
    public int NextVersion() => Interlocked.Increment(ref _nextVersion);

    public async Task EnsureStarted(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        using SemaphoreLease lease = await _gate.Acquire(linked.Token);
        _connection ??= await live.Board(_id, ApplySnapshot, Restored, Disconnected, _lifetime.Token);
        if (!IsConnected) await _connection.Start(_lifetime.Token);
        totals.UpdateLive(IsConnected);
        _activityClock ??= SampleLiveActivity();
    }

    private async Task SampleLiveActivity()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                LiveActivity.Sample(totals);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async Task Configure(int version, string query, int offset, int count, DateTimeOffset? startAt,
        DateTimeOffset? endAt, CancellationToken cancellationToken, string? excludedStates = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (version < _version) return;
        _version = version;
        _query = query;
        _excludedStates = excludedStates;
        _offset = offset;
        _count = count;
        _startAt = startAt;
        _endAt = endAt;
        await EnsureStarted(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (version == _version) await Subscribe(cancellationToken);
    }

    private async Task Subscribe(CancellationToken cancellationToken, bool force = false)
    {
        using SemaphoreLease lease = await _subscribeGate.Acquire(cancellationToken);
        if (_connection is null || (!force && _subscribedVersion == _version)) return;
        int version = _version;
        await _connection.SubscribeBoard(version, _query, _offset, _count, _startAt, _endAt, cancellationToken, _excludedStates);
        _subscribedVersion = version;
    }

    private async Task Restored()
    {
        if (_lifetime.IsCancellationRequested) return;
        await Subscribe(_lifetime.Token, force: true);
        totals.UpdateLive(true);
    }

    private Task Disconnected()
    {
        if (!_lifetime.IsCancellationRequested) totals.UpdateLive(false);
        return Task.CompletedTask;
    }

    private async Task ApplySnapshot(LiveBoard snapshot)
    {
        if (_lifetime.IsCancellationRequested || snapshot.Version != _version) return;
        Latest = snapshot;
        totals.UpdateSnapshot(snapshot);
        if (snapshot.LiveActivity is { } activity) LiveActivity.Update(activity, totals);
        if (Snapshot is { } handlers)
            foreach (Func<LiveBoard, Task> handler in handlers.GetInvocationList())
                await handler(snapshot);
    }

    public async Task Stop()
    {
        using SemaphoreLease lease = await _gate.Acquire();
        _version = NextVersion();
        _subscribedVersion = -1;
        if (_connection is not null) await _connection.DisposeAsync();
        _connection = null;
        Latest = null;
        LiveActivity = new();
        _query = "";
        _excludedStates = null;
        _offset = 0;
        _count = 1;
        _startAt = _endAt = null;
        totals.Clear();
    }

    public async Task ReleaseQuery(int version)
    {
        if (_lifetime.IsCancellationRequested || version != _version || !IsConnected) return;
        totals.LastHour = true;
        if (Latest is { } snapshot) totals.UpdateSnapshot(snapshot);
        try { await Configure(NextVersion(), "", 0, 1, null, null, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { totals.UpdateLive(IsConnected); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync();
        if (_activityClock is not null) await _activityClock;
        await Stop();
        Snapshot = null;
    }
}
