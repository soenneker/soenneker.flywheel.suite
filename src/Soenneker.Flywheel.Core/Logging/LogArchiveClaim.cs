namespace Soenneker.Flywheel.Core.Logging;

/// <summary>An exclusive, time-limited claim on an immutable archive file.</summary>
public sealed record LogArchiveClaim(LogArchiveFile File, string Token, long AvailableAt);
