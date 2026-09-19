using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Dashboard;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    [Test]
    public async Task DeadLetterFilterExcludesNewScheduledAndQueuedExecutions()
    {
        var failed = new JobRecord { Id = "failed", Name = "invoice", Payload = "{}", Policy = new JobPolicy(), State = JobState.DeadLettered };
        var store = new SearchStore { SearchItems = [failed] };
        const string excluded = "Scheduled,Queued,Running,Succeeded,Cancelled,Waiting";
        JobSearchResult before = await DashboardJobSearch.Search(store, "", 0, 50, null, null, excluded, default);
        Check(before.TotalCount == 1, "The dead-lettered execution was missing.");
        store.SearchItems = [failed,
            new JobRecord { Id = "scheduled", Name = "recurring", Payload = "{}", Policy = new JobPolicy(), State = JobState.Scheduled, DueAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() },
            new JobRecord { Id = "queued", Name = "recurring", Payload = "{}", Policy = new JobPolicy(), State = JobState.Scheduled, DueAt = 0 }];
        JobSearchResult after = await DashboardJobSearch.Search(store, "", 0, 50, null, null, excluded, default);
        Check(after.TotalCount == 1 && after.Items.Count == 1 && after.Items[0].Id == "failed", "New executions leaked into the dead-letter filter.");
    }

    [Test]
    public async Task StatusFiltersApplyBeforePaginationAndPreserveSearch()
    {
        var store = new SearchStore
        {
            SearchItems = Enumerable.Range(0, 450).Select(i => new JobRecord
            {
                Id = i.ToString(), Name = "invoice", Payload = "{}", Policy = new JobPolicy(),
                State = i % 2 == 0 ? JobState.Succeeded : JobState.Running
            }).ToArray()
        };
        JobSearchResult result = await DashboardJobSearch.Search(store, "invoice", 50, 25, null, null, "Succeeded", default);
        Check(result.TotalCount == 225 && result.Items.Count == 25 && result.Items[0].Id == "101" &&
            result.Items.All(job => job.State == JobState.Running), "Status exclusions were applied after pagination");
        JobSearchResult empty = await DashboardJobSearch.Search(store, "invoice", 0, 25, null, null, "Succeeded,Running", default);
        Check(empty.TotalCount == 0 && empty.Items.Count == 0, "Hiding all matching states still returned jobs");
        JobSearchResult search = await DashboardJobSearch.Search(store, "missing", 0, 25, null, null, "Succeeded", default);
        Check(search.TotalCount == 0, "Status filtering discarded the text search");
        Check(!DashboardJobSearch.IsValid("bogus"), "Invalid statuses were accepted");
    }
}
