using Soenneker.Atomics.ValueInts;

namespace Soenneker.Flywheel.Core.Services;

internal sealed class WorkerState
{
    internal ValueAtomicInt Retiring;
    internal Task Task { get; set; } = Task.CompletedTask;
}
