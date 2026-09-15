namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Distributed node liveness, based on authoritative storage time.</summary>
public interface INodeStore
{
    /// <summary>Records the node heartbeat, active worker capacity, and recent busy-worker history with an expiry; it is diagnostic, not execution ownership.</summary>
    Task Heartbeat(string node, int workers, TimeSpan ttl, CancellationToken cancellationToken = default);
}
