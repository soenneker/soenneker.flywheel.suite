using Soenneker.Atomics.ValueInts;

namespace Soenneker.Flywheel.Core.Services;

internal sealed class WorkerState(CancellationToken stoppingToken)
{
    internal readonly CancellationTokenSource Wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    internal ValueAtomicInt Retiring;
    internal Task Task { get; set; } = Task.CompletedTask;
}
