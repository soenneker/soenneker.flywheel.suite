using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Dashboard;
using Soenneker.Flywheel.Communication.Enums;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    [Test]
    public async Task StatusFiltersApplyBeforePaginationAndPreserveSearch()
    {
        var store = new SearchStore
        {
            SearchItems = Enumerable.Range(0, 450).Select(i => new JobRecord
            {
                Id = i.ToString(), Name = "invoice", Payload = "{}", Policy = new(),
                State = i % 2 == 0 ? JobState.Succeeded : JobState.Running
            }).ToArray()
        };
        var result = await DashboardJobSearch.Search(store, "invoice", 50, 25, null, null, "Succeeded", default);
        Check(result.TotalCount == 225 && result.Items.Count == 25 && result.Items[0].Id == "101" &&
            result.Items.All(job => job.State == JobState.Running), "Status exclusions were applied after pagination");
        var empty = await DashboardJobSearch.Search(store, "invoice", 0, 25, null, null, "Succeeded,Running", default);
        Check(empty.TotalCount == 0 && empty.Items.Count == 0, "Hiding all matching states still returned jobs");
        var search = await DashboardJobSearch.Search(store, "missing", 0, 25, null, null, "Succeeded", default);
        Check(search.TotalCount == 0, "Status filtering discarded the text search");
        Check(!DashboardJobSearch.IsValid("bogus"), "Invalid statuses were accepted");
    }
}
