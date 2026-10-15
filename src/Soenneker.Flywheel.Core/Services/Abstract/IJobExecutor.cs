namespace Soenneker.Flywheel.Core.Services.Abstract;

/// <summary>Executes one durable claim in a DI scope, renewing ownership and cancelling on uncertainty.</summary>
public interface IJobExecutor
{
    /// <summary>Attempts one execution. False means no due work. Lost leases never commit outcomes.</summary>
    Task<bool> RunOnce(CancellationToken cancellationToken);

    /// <summary>Attempts one execution and signals available work after claiming, before invoking the handler.
    /// The callback must not throw.</summary>
    Task<bool> RunOnce(Action onClaimed, CancellationToken cancellationToken);

    /// <summary>Attempts one execution from the specified case-sensitive pool, preserving separate admission and execution cancellation.</summary>
    Task<bool> RunOnce(string pool, Action onClaimed, CancellationToken claimingToken, CancellationToken executionToken) =>
        pool == "default" ? RunOnce(onClaimed, claimingToken, executionToken)
            : throw new NotSupportedException("This executor does not support named pools.");

    /// <summary>Uses separate tokens to stop admission immediately while allowing a claimed handler to drain.
    /// Implementations supporting graceful draining must preserve this distinction.</summary>
    Task<bool> RunOnce(Action onClaimed, CancellationToken claimingToken, CancellationToken executionToken) =>
        RunOnce(onClaimed, executionToken);
}
