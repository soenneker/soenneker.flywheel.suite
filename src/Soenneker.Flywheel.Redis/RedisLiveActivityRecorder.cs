using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Redis;

/// <summary>Records live queue and worker counts independently of dashboard connections.</summary>
public sealed class RedisLiveActivityRecorder(RedisJobStore store, ILogger<RedisLiveActivityRecorder> logger) : BackgroundService
{
    private int _idle;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var changed = new SemaphoreSlim(0, 1);
        using var watchStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task watcher = Watch(changed, watchStop.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Arm before reading so a notification racing an empty sample cannot be lost.
                Volatile.Write(ref _idle, 1);
                bool active = false;
                try { active = await store.SampleLiveActivity(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) { logger.LogWarning(exception, "Could not record Flywheel live activity."); }
                if (active) Volatile.Write(ref _idle, 0);
                await changed.WaitAsync(TimeSpan.FromSeconds(active ? 1 : 15), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await watchStop.CancelAsync();
            await watcher;
        }
    }

    private async Task Watch(SemaphoreSlim changed, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await foreach (JobChange change in store.Watch(token))
                    if (change.Kind is "Job" or "Resync" && Interlocked.Exchange(ref _idle, 0) != 0 && changed.CurrentCount == 0)
                        changed.Release();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Could not subscribe to Flywheel live activity."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}
