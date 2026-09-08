namespace Soenneker.Flywheel.Core.Services.Abstract;

/// <summary>Executes one durable claim in a DI scope, renewing ownership and cancelling on uncertainty.</summary>
public interface IJobExecutor
{
    /// <summary>Attempts one execution. False means no due work. Lost leases never commit outcomes.</summary>
    Task<bool> RunOnce(CancellationToken cancellationToken);
}
