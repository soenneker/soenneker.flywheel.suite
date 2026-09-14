using Soenneker.Flywheel.Communication.Logging.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed class LogBuffer
{
    public List<JobLogEntry> Entries { get; set; } = [];
    public long ExpiresAt { get; set; }
}
