namespace Soenneker.Flywheel.Core.Services.Abstract;

/// <summary>Independent scheduler/recovery iteration, safe to run concurrently on all nodes.</summary>
public interface IMaintenanceRunner
{
    /// <summary>Recovers expired work, materializes schedules and refreshes the node heartbeat.</summary>
    Task RunOnce(CancellationToken cancellationToken);
}
