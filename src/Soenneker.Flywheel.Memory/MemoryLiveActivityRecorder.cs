using Microsoft.Extensions.Hosting;

namespace Soenneker.Flywheel.Memory;

internal sealed class MemoryLiveActivityRecorder(MemoryJobStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do { await store.SampleLiveActivity(stoppingToken).ConfigureAwait(false); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
