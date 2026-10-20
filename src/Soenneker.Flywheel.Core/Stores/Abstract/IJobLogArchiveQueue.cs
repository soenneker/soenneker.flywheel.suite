using Soenneker.Flywheel.Core.Logging;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Durable archive outbox committed atomically with accepted job logs.</summary>
public interface IJobLogArchiveQueue
{
    /// <summary>Seals aged files and claims one ready file. Expired claims can be retried.</summary>
    Task<LogArchiveClaim?> ClaimLogArchive(CancellationToken cancellationToken = default);

    /// <summary>Removes a successfully saved file only if the claim token is still current.</summary>
    Task CompleteLogArchive(LogArchiveClaim claim, CancellationToken cancellationToken = default);
}
