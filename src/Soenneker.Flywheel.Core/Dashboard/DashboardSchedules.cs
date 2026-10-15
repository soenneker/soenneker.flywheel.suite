using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Core.Dashboard.Abstract;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard;

internal static class DashboardSchedules
{
    internal static async Task<ScheduleView> Read(IJobScheduleStore store, IDashboardSnapshotFactory snapshots, CancellationToken ct)
    {
        ScheduleView view = snapshots.Schedules(await store.ListRecurring(200, ct), await store.ListScheduled(200, ct));
        return view with
        {
            QueueCounts = store is IJobQueueCountStore counts ? await counts.GetQueueCounts(ct) : null,
            RecurringCount = store is IRecurringJobCountStore recurring ? await recurring.GetRecurringCount(ct) : null
        };
    }
}
