using Microsoft.AspNetCore.SignalR;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Core.Dashboard;

internal sealed class DashboardSessionFilter(DashboardOptions options) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (!DashboardIdentity.Valid(options, context.Context.User))
        {
            context.Context.Abort();
            throw new HubException("The dashboard session has been revoked.");
        }
        return await next(context);
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        if (!DashboardIdentity.Valid(options, context.Context.User)) { context.Context.Abort(); return; }
        await next(context);
        _ = Monitor(context.Context);
    }

    private async Task Monitor(HubCallerContext context)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(context.ConnectionAborted))
                if (!DashboardIdentity.Valid(options, context.User)) { context.Abort(); return; }
        }
        catch (OperationCanceledException) when (context.ConnectionAborted.IsCancellationRequested) { }
    }
}
