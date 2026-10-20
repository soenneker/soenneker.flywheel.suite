namespace Soenneker.Flywheel.Core.Logging;

/// <summary>Durable destination for immutable job log files.</summary>
public interface IJobLogArchive
{
    /// <summary>Saves a file durably. Repeating the same file ID must be safe.
    /// Throws on failure so the durable source queue retains the file for retry.</summary>
    Task Save(LogArchiveFile file, CancellationToken cancellationToken = default);
}
