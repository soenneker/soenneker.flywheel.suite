using Soenneker.Flywheel.Core.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Cross-node notifications of committed changes, including a resync after subscription or reconnect.</summary>
public interface IJobChangeFeed
{
    /// <summary>Subscribes before yielding the initial Resync. Consumers must reread snapshots on Resync,
    /// including after disconnects or a bounded buffer overflow. Notifications are hints, not a durable event log.</summary>
    IAsyncEnumerable<JobChange> Watch(CancellationToken cancellationToken = default);
}
