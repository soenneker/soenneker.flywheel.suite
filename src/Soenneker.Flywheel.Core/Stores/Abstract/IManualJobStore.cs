using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Persists reusable job definitions that execute only when explicitly requested.</summary>
public interface IManualJobStore
{
    /// <summary>Registers a manual job if absent. Existing payload and policy remain unchanged. No execution is queued.</summary>
    Task<bool> AddManual(string id, EnqueueRequest request, CancellationToken cancellationToken = default);
    /// <summary>Queues a fresh execution, or returns null if the definition does not exist.</summary>
    Task<string?> RunManual(string id, CancellationToken cancellationToken = default);
    /// <summary>Lists definitions ordered by ID, with the latest execution status. Count must be between 1 and 200.</summary>
    Task<IReadOnlyList<ManualJobView>> ListManual(int count = 50, CancellationToken cancellationToken = default);
}
