using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal readonly record struct Node(long ExpiresAt, int Workers)
{
    public ServerWorkerHistoryPoint[]? WorkerHistory { get; init; }
}
