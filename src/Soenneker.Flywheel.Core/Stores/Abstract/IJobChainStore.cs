using Soenneker.Flywheel.Communication.Requests;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Persists ordered job chains and releases each successor atomically with predecessor success.</summary>
public interface IJobChainStore
{
    /// <summary>Atomically saves 1–100 steps and returns IDs in order. Final failure or cancellation cancels remaining steps.
    /// An optional chain idempotency key returns the original IDs on repeated submission. Step idempotency keys are not supported.</summary>
    Task<IReadOnlyList<string>> EnqueueChain(IReadOnlyList<EnqueueRequest> steps, string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}
