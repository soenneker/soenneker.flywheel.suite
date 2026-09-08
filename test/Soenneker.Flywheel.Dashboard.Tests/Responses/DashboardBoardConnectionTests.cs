using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardBoardConnectionTests
{
    [Test]
    public async Task NavigationReusesTransportWithoutResettingStatusOrCancellingItsLifetime()
    {
        var client = new BoardConnectionTestClient();
        var totals = new ActivityTotalsState();
        await using var connection = new DashboardBoardConnection(client, totals);
        using var firstPage = new CancellationTokenSource();
        int first = connection.NextVersion();
        await connection.Configure(first, "first", 0, 50, null, null, firstPage.Token);
        Check(client.Transport.Subscriptions == 1, "Startup subscribed twice.");
        await firstPage.CancelAsync();
        int second = connection.NextVersion();
        await connection.Configure(second, "second", 0, 10, null, null, CancellationToken.None);
        Check(client.Connections == 1 && !client.Transport.Disposed, "Navigation recreated the socket.");
        Check(totals.Live && !client.Lifetime.IsCancellationRequested, "Page cancellation reset the shell connection.");
        Check(client.Transport.Version == second && client.Transport.Query == "second", "Navigation did not replace the subscription.");
        await connection.Configure(first, "stale", 0, 50, null, null, CancellationToken.None);
        Check(client.Transport.Query == "second", "An old page replaced the current subscription.");
    }

    [Test]
    public async Task RealDisconnectsUpdateStatusAndRecoveryRestoresTheCurrentQuery()
    {
        var client = new BoardConnectionTestClient();
        var totals = new ActivityTotalsState();
        await using var connection = new DashboardBoardConnection(client, totals);
        int version = connection.NextVersion();
        await connection.Configure(version, "current", 0, 50, null, null, CancellationToken.None);
        client.Transport.IsConnected = false;
        await client.Disconnected();
        Check(!totals.Live, "A real disconnection was hidden.");
        client.Transport.IsConnected = true;
        await client.Restored();
        Check(totals.Live && client.Transport.Subscriptions == 2 && client.Transport.Query == "current", "Recovery did not restore the current query.");
        await connection.Stop();
        Check(client.Transport.Disposed && !totals.Live, "Sign-out left a socket or connected status behind.");
        await connection.EnsureStarted();
        Check(client.Connections == 2 && totals.Live, "A later shell could not reconnect.");
    }

    [Test]
    public async Task HeaderUpdatesWithoutAPageAndIgnoresStaleAndUnchangedSnapshots()
    {
        var client = new BoardConnectionTestClient();
        var totals = new ActivityTotalsState();
        await using var connection = new DashboardBoardConnection(client, totals);
        await connection.EnsureStarted();
        int changes = 0;
        totals.Changed += () => changes++;
        var snapshot = new LiveBoard(0, [], 0, [], new ScheduleView([], []), 2, 1, 4);
        await client.Snapshot(snapshot);
        await client.Snapshot(snapshot);
        Check(changes == 1 && totals.RunningCount == 2 && totals.TotalWorkers == 4, "Header updates were missing or redundant.");
        int version = connection.NextVersion();
        await connection.Configure(version, "next", 0, 50, null, null, CancellationToken.None);
        await client.Snapshot(snapshot with { RunningCount = 99 });
        Check(totals.RunningCount == 2, "A stale snapshot overwrote the current header.");
    }

    [Test]
    public async Task LiveChartRetainsSamplesAcrossNavigationAndClearsOnSignOut()
    {
        var client = new BoardConnectionTestClient();
        var totals = new ActivityTotalsState();
        await using var connection = new DashboardBoardConnection(client, totals);
        int first = connection.NextVersion();
        await connection.Configure(first, "", 0, 50, null, null, CancellationToken.None);
        var snapshot = new LiveBoard(first, [], 0, [], new ScheduleView([], []), 3, 1, 4)
        {
            LiveActivity = [new(60000, 0, 0, 2, 0)]
        };
        await client.Snapshot(snapshot);
        var activity = connection.LiveActivity;
        activity.Advance(totals);
        await connection.ReleaseQuery(first);
        await client.Snapshot(snapshot with { Version = client.Transport.Version });
        int next = connection.NextVersion();
        await connection.Configure(next, "", 0, 50, null, null, CancellationToken.None);
        Check(ReferenceEquals(activity, connection.LiveActivity), "Navigation discarded chart state.");
        connection.LiveActivity.Advance(totals);
        Check(activity.Data.Series[1].Values[^1] == 3 && activity.Data.Series[2].Values.Contains(2),
            "Returning to the dashboard lost concurrency or completed activity.");
        await connection.Stop();
        Check(!ReferenceEquals(activity, connection.LiveActivity), "Sign-out retained the previous session's chart.");
    }

    [Test]
    public async Task LiveChartSamplesWhileNoDashboardPageOrNewSnapshotsExist()
    {
        var client = new BoardConnectionTestClient();
        var totals = new ActivityTotalsState();
        await using var connection = new DashboardBoardConnection(client, totals);
        await connection.EnsureStarted();
        await client.Snapshot(new LiveBoard(0, [], 0, [], new ScheduleView([], []), 3, 1, 4)
        {
            LiveActivity = [new(60000, 0, 0, 2, 0)]
        });
        // No page calls Advance, and unchanged server state sends no new snapshots.
        await Task.Delay(3200);
        var state = connection.LiveActivity;
        var samples = (Dictionary<long, double?>)typeof(DashboardLiveActivityState)
            .GetField("_runningSamples", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(state)!;
        Check(samples.TryGetValue(61000, out double? running) && running == 3,
            "The shared clock did not sample while the dashboard was absent.");
        state.Advance(totals);
        for (int i = 0; i < state.Data.XValues.Count; i++)
            if (state.Data.XValues[i] >= 60000)
                Check(state.Data.Series[1].Values[i] == 3, "Navigation left a gap in the running series.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
