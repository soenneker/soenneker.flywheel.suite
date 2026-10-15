using Microsoft.AspNetCore.Http;
using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Core.Dashboard.Endpoints;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    [Test]
    public async Task QueueEndpointValidatesActionsAndReportsStaleExecutions(CancellationToken cancellationToken)
    {
        var store = new SearchStore();
        foreach ((string action, JobQueueAction expected) in new[]
        {
            ("top", JobQueueAction.MoveToTop), ("bottom", JobQueueAction.MoveToBottom), ("run-now", JobQueueAction.RunNow)
        })
        {
            var result = (IStatusCodeHttpResult)await FlywheelJobsEndpoints.UpdateQueue(store, "one", action, cancellationToken);
            Check(result.StatusCode == 204 && store.LastQueueAction == expected, "Queue action was not passed to storage.");
        }
        Check(((IStatusCodeHttpResult)await FlywheelJobsEndpoints.UpdateQueue(store, "one", "invalid", cancellationToken)).StatusCode == 400,
            "Unknown action accepted.");
        Check(((IStatusCodeHttpResult)await FlywheelJobsEndpoints.UpdateQueue(store, "missing", "top", cancellationToken)).StatusCode == 404,
            "Missing execution did not return 404.");
        store.AcceptQueueAction = false;
        Check(((IStatusCodeHttpResult)await FlywheelJobsEndpoints.UpdateQueue(store, "one", "top", cancellationToken)).StatusCode == 409,
            "Stale execution did not return 409.");
    }
}
