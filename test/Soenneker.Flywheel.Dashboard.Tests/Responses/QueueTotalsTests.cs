using Soenneker.Flywheel.Communication;
using Soenneker.Flywheel.Communication.Responses;
using System.Text.Json;
using Soenneker.Flywheel.Dashboard.Communication;

namespace Soenneker.Flywheel.Dashboard.Tests.Responses;

public sealed class QueueTotalsTests
{
    [Test]
    public async Task UsesFullCountsInsteadOfDisplayedRows(CancellationToken cancellationToken)
    {
        var schedules = new ScheduleView([], []) { QueueCounts = new JobQueueCounts(1200, 450), RecurringCount = 300 };
        var board = new LiveBoard(1, [], 0, null, schedules);
        string json = JsonSerializer.Serialize(board, FlywheelJsonContext.Get<LiveBoard>());
        using var document = JsonDocument.Parse(json);
        var restored = await DashboardBoardReader.Read(document.RootElement, cancellationToken);
        var totals = new ActivityTotalsState();
        totals.UpdateSnapshot(restored);
        if (totals.QueuedCount != 1200 || totals.ScheduledCount != 450 || restored.Schedules?.RecurringCount != 300)
            throw new Exception("Exact counts did not survive transport and dashboard update.");
        totals.UpdateSnapshot(board with { Schedules = new ScheduleView([], []) });
        if (totals.QueuedCount is not null || totals.ScheduledCount is not null)
            throw new Exception("Unsupported totals must not be reported as zero or a truncated count.");
    }
}
