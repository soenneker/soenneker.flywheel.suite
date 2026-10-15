using Soenneker.Atomics.ValueInts;

namespace Soenneker.Flywheel.Core.Services;

internal sealed class WorkerState(WorkerPoolState pool, CancellationToken stoppingToken)
{
    internal WorkerPoolState Pool { get; } = pool;
    internal readonly CancellationTokenSource Wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    internal ValueAtomicInt Retiring;
    internal Task Task { get; set; } = Task.CompletedTask;
}
