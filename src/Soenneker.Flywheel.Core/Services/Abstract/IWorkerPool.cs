using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Services.Abstract;

/// <summary>Controls the number of job workers running on the current host.</summary>
public interface IWorkerPool
{
    /// <summary>Gets the desired number of workers. Workers retiring from active jobs are not included.</summary>
    int WorkerCount { get; }

    /// <summary>Gets each configured pool's desired capacity, excluding retiring executions.</summary>
    IReadOnlyList<WorkerPoolCapacity> Pools => [new("default", WorkerCount)];

    /// <summary>Changes one configured pool's capacity without affecting other pools. Zero pauses admission.
    /// Removed workers finish their current job before retiring and continue occupying execution capacity until they exit.
    /// With named pools, the sum cannot exceed the configured worker budget.</summary>
    ValueTask SetPoolWorkerCount(string pool, int count, CancellationToken cancellationToken = default) =>
        pool == "default" ? SetWorkerCount(count, cancellationToken) : throw new NotSupportedException("Named pools are not supported.");

    /// <summary>Changes the worker count when exactly one pool is configured. Added workers wait for retiring executions to release capacity; removed workers finish their current job before retiring.</summary>
    ValueTask SetWorkerCount(int count, CancellationToken cancellationToken = default);
}
