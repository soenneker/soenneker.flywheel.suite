using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Soenneker.Flywheel.Redis;

/// <summary>Records live queue and worker counts independently of dashboard connections.</summary>
public sealed class RedisLiveActivityRecorder(RedisJobStore store, ILogger<RedisLiveActivityRecorder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try { await store.RecordLiveActivity(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) { logger.LogWarning(exception, "Could not record Flywheel live activity."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
