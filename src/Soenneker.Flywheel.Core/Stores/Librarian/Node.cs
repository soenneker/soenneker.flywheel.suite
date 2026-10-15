using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal readonly record struct Node(long ExpiresAt, int Workers)
{
    public WorkerPoolCapacity[]? Pools { get; init; }

    public ServerWorkerHistoryPoint[]? WorkerHistory { get; init; }
}
