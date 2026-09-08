using System.Reflection;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class ScheduleFilterTests
{
    [Test]
    public void RecurringSearchReusesResultsAndInvalidatesOnQueryOrSnapshotChanges()
    {
        var board = new FlywheelBoard();
        var schedules = new ScheduleView([new("daily", "Report", 60000, 0), new("weekly", "Cleanup", 60000, 0)], []);
        Set("_schedules", schedules);
        if (!ReferenceEquals(Read(), schedules.Recurring)) throw new Exception("Empty search should reuse the source list");
        Set("_recurringSearch", "report daily");
        var filtered = Read();
        if (filtered.Count != 1 || filtered[0].Id != "daily") throw new Exception("Cross-field search changed");
        if (!ReferenceEquals(filtered, Read())) throw new Exception("Unchanged search was recomputed");
        Set("_recurringSearch", "CLEANUP");
        if (Read().Count != 1 || Read()[0].Id != "weekly") throw new Exception("Query change did not invalidate the cache");
        Set("_schedules", new ScheduleView([], []));
        if (Read().Count != 0) throw new Exception("Snapshot change did not invalidate the cache");
        Set("_schedules", null);
        if (Read().Count != 0) throw new Exception("Cleared schedules left stale results");

        void Set(string name, object? value) => typeof(FlywheelBoard).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(board, value);
        IReadOnlyList<RecurringScheduleView> Read() => (IReadOnlyList<RecurringScheduleView>)typeof(FlywheelBoard)
            .GetProperty("FilteredRecurring", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(board)!;
    }
}
