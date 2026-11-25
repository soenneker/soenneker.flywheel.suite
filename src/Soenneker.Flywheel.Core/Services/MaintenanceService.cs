using Soenneker.Flywheel.Core.Services.Abstract;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Core.Services;

public sealed class MaintenanceService(IMaintenanceRunner runner, FlywheelOptions options, ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (runner is not MaintenanceRunner builtin)
            return Loop(runner.RunOnce, options.MaintenanceInterval, "maintenance", stoppingToken);

        TimeSpan heartbeatInterval = TimeSpan.FromTicks(Math.Min(options.MaintenanceInterval.Ticks, options.LeaseDuration.Ticks / 3));
        return Task.WhenAll(Loop(builtin.Heartbeat, heartbeatInterval, "heartbeat", stoppingToken),
            Loop(builtin.Maintain, options.MaintenanceInterval, "maintenance", stoppingToken));
    }

    private async Task Loop(Func<CancellationToken, Task> run, TimeSpan interval, string operation, CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                try { await run(token); }
                catch (Exception ex) when (!token.IsCancellationRequested) { logger.LogError(ex, "Flywheel {Operation} failed", operation); }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
