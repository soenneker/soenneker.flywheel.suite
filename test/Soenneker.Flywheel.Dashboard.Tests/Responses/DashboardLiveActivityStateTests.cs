using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardLiveActivityStateTests
{
    [Test]
    public void ClockTicksAppendWithoutRebuildingRetainedSamples()
    {
        var clock = new ActivityClock();
        var state = new DashboardLiveActivityState(clock);
        var totals = Totals();
        state.Update([new JobHistoryPoint(60000, 0, 0, 2, 0)], totals);
        state.Advance(totals);
        string retainedLabel = state.Data.Labels[1];
        long version = state.Data.Version;
        state.Advance(totals);
        Check(state.Data.Version == version, "An unchanged bucket invalidated the chart.");

        clock.Advance(1);
        state.Advance(totals);
        Check(state.Data.Version == version + 1, "A clock tick rebuilt the whole window.");
        Check(state.Data.Count == 61 && state.Data.XValues[0] == 1000 && state.Data.XValues[^1] == 61000, "The rolling window changed size or ordering.");
        Check(ReferenceEquals(retainedLabel, state.Data.Labels[0]), "A retained label was reformatted.");
        Check(state.Data.Series[2].Values[^2] == 2 && state.Data.Series[2].Values[^1] == 0, "A retained event count was lost.");
    }

    [Test]
    public void ServerCorrectionsReplaceValuesButIdenticalSnapshotsDoNotRenderAgain()
    {
        var state = new DashboardLiveActivityState(new ActivityClock());
        var totals = Totals();
        var point = new JobHistoryPoint(60000, 0, 0, 2, 0) { RunningCount = 1 };
        state.Update([point], totals);
        state.Advance(totals);
        long version = state.Data.Version;
        state.Update([point], totals);
        state.Advance(totals);
        Check(state.Data.Version == version, "An identical server snapshot rebuilt samples.");
        state.Update([point with { Succeeded = 9, RunningCount = 4 }], totals);
        state.Advance(totals);
        Check(state.Data.Series[2].Values[^1] == 9 && state.Data.Series[1].Values[^1] == 4, "Late server corrections were ignored.");
        Check(state.Data.Count == 61 && state.Data.XValues[^1] == 60000, "A correction moved the time window.");
    }

    [Test]
    public void LateTicksAndLongPausesKeepABoundedWindowAndDisconnectedGaps()
    {
        var clock = new ActivityClock();
        var state = new DashboardLiveActivityState(clock);
        var totals = Totals();
        state.Update([new JobHistoryPoint(60000, 0, 0, 2, 0)], totals);
        state.Advance(totals);
        long version = state.Data.Version;
        clock.Advance(3);
        state.Advance(totals);
        Check(state.Data.Version == version + 3, "A delayed tick did not append the missing seconds.");
        Check(state.Data.Series[1].Values.TakeLast(3).All(value => value == 3), "Missing seconds did not retain the last observed gauge.");
        totals.UpdateLive(false);
        clock.Advance(1);
        state.Advance(totals);
        Check(state.Data.Series[1].Values[^1] is null, "A disconnected sample invented a gauge.");
        clock.Advance(120);
        state.Advance(totals);
        Check(state.Data.Count == 61 && state.Data.XValues[^1] - state.Data.XValues[0] == 60000, "A long pause created an unbounded window.");
        Check(state.Data.Series[1].Values.All(value => value is null), "A long disconnection filled gaps with data.");
    }

    private static ActivityTotalsState Totals()
    {
        var totals = new ActivityTotalsState();
        totals.UpdateLive(true);
        totals.UpdateRunning(3);
        return totals;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class ActivityClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(int seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
