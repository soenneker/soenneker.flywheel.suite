namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Distributed node liveness, based on authoritative storage time.</summary>
public interface INodeStore
{
    /// <summary>Records the node heartbeat and active worker capacity with an expiry; it is diagnostic, not execution ownership.</summary>
    Task Heartbeat(string node, int workers, TimeSpan ttl, CancellationToken cancellationToken = default);
}
