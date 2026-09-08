using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Soenneker.Flywheel.Communication.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard;

[Authorize(Policy = "FlywheelDashboard")]
public sealed class FlywheelHub(DashboardSubscriptions subscriptions) : Hub<IFlywheelDashboardClient>, IFlywheelDashboardHub
{
    public Task SubscribeBoard(int version, string? query, int offset, int count, bool summary, DateTimeOffset? startAt = null, DateTimeOffset? endAt = null) =>
        subscriptions.Subscribe(Context.ConnectionId, "Board", version, query, offset, count, summary, null, Context.ConnectionAborted, startAt, endAt);

    public Task SubscribeFilteredBoard(int version, string? query, int offset, int count, bool summary, DateTimeOffset? startAt, DateTimeOffset? endAt, string? excludedStates) =>
        subscriptions.Subscribe(Context.ConnectionId, "Board", version, query, offset, count, summary, null, Context.ConnectionAborted, startAt, endAt, excludedStates);

    public Task SubscribeJob(int version, string jobId) =>
        subscriptions.Subscribe(Context.ConnectionId, "Job", version, null, 0, 1, false, jobId, Context.ConnectionAborted);

    public Task SubscribeLogs(int version, string jobId) =>
        subscriptions.Subscribe(Context.ConnectionId, "Logs", version, null, 0, 1, false, jobId, Context.ConnectionAborted);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await subscriptions.Remove(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
