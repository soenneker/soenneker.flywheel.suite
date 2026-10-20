namespace Soenneker.Flywheel.Core.Logging;

/// <summary>A rolling archive file containing logs from multiple jobs.</summary>
public sealed record LogArchiveFile(string Id, long CreatedAt, IReadOnlyList<LogArchiveEntry> Entries);
