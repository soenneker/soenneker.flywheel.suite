using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Distributed node liveness, based on authoritative storage time.</summary>
public interface INodeStore
{
    /// <summary>Records the node heartbeat, active worker capacity, and recent busy-worker history with an expiry; it is diagnostic, not execution ownership.</summary>
    Task Heartbeat(string node, int workers, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Records node liveness and each case-sensitive pool's worker capacity. Pool names must be unique;
    /// total capacity must not exceed 256. Zero workers allows a configured pool to be paused.</summary>
    Task Heartbeat(string node, IReadOnlyList<WorkerPoolCapacity> pools, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        pools.Count == 1 && pools[0].Name == "default" ? Heartbeat(node, pools[0].Workers, ttl, cancellationToken)
            : throw new NotSupportedException("This node store does not support worker pools.");
}
