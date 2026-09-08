using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Communication.Dtos;
using Soenneker.Flywheel.Core.Stores.Abstract;

namespace Soenneker.Flywheel.Core.Dashboard;

public sealed class DashboardNotifications(IJobStore store, DashboardSubscriptions subscriptions,
    ILogger<DashboardNotifications> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (store is not IJobChangeFeed feed)
            throw new InvalidOperationException("Live dashboards require an IJobChangeFeed storage provider.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (JobChange change in feed.Watch(stoppingToken)) subscriptions.Changed(change);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Dashboard change subscription unavailable: {Error}", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); // Reconnect failed subscriptions only.
            }
        }
    }
}
