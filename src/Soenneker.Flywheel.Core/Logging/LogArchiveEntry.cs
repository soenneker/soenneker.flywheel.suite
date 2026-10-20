using Soenneker.Flywheel.Communication.Logging.Dtos;

namespace Soenneker.Flywheel.Core.Logging;

/// <summary>A captured log entry with its originating job identity.</summary>
public sealed record LogArchiveEntry(string JobId, JobLogEntry Log);
