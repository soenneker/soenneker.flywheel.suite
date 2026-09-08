using Soenneker.Flywheel.Core.Responses;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Reads live worker servers and their currently leased jobs for diagnostics.</summary>
public interface IServerStore
{
    /// <summary>Lists live servers and their currently leased jobs, ordered by heartbeat expiration.</summary>
    Task<IReadOnlyList<ServerView>> ListServers(int count = 200, CancellationToken cancellationToken = default);

    /// <summary>Returns a live server and its currently leased jobs, or null after its heartbeat expires.</summary>
    Task<ServerView?> GetServer(string node, CancellationToken cancellationToken = default);

    /// <summary>Returns the active worker capacity reported by all servers with unexpired heartbeats.</summary>
    Task<int> GetTotalWorkerCount(CancellationToken cancellationToken = default);
}
