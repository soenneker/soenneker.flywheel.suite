using Soenneker.Flywheel.Communication.Enums;
using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Atomic conditional dependencies and parallel batches. All workers must support workflows before use.</summary>
public interface IJobWorkflowStore
{
    /// <summary>Attaches a new job to a retained execution, even if it has already finished. Missing parents throw KeyNotFoundException.
    /// Retries keep children waiting. An unmet condition skips the new job and its descendants. Delay starts when the dependency is satisfied.
    /// A retained idempotency key deduplicates the submission. Workflows contain at most 1000 executions.</summary>
    Task<string> ContinueWith(string parentId, EnqueueRequest request, ContinuationCondition condition = ContinuationCondition.OnSuccess,
        string? idempotencyKey = null, CancellationToken cancellationToken = default);
    /// <summary>Atomically submits 1–999 independent jobs and an optional dependent final step. The final step waits for all members to finish.
    /// Only OnSuccess and OnCompleted are accepted for batch completion. Member failure does not cancel other members.
    /// Step idempotency keys are unsupported; reuse the batch key for uncertain submissions.</summary>
    Task<WorkflowSubmission> EnqueueBatch(IReadOnlyList<EnqueueRequest> jobs, EnqueueRequest? continuation = null,
        ContinuationCondition condition = ContinuationCondition.OnSuccess, string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
    /// <summary>Reads the workflow containing a retained execution, or null when it has no workflow.</summary>
    Task<WorkflowView?> GetWorkflow(string jobId, CancellationToken cancellationToken = default);
}
