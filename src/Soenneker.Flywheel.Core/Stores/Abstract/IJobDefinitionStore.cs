using Soenneker.Flywheel.Communication.Requests;
using Soenneker.Flywheel.Communication.Responses;
namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Edits saved definitions without modifying existing executions or their history.</summary>
public interface IJobDefinitionStore
{
    /// <summary>Reads a recurring definition, including its payload, or null if missing.</summary>
    Task<ScheduleDefinitionView?> GetRecurringDefinition(string id, CancellationToken cancellationToken = default);
    /// <summary>Replaces an existing recurring definition and recalculates its next occurrence. Returns false if missing, system-owned, or the revision changed.</summary>
    Task<bool> UpdateRecurring(string id, ScheduleUpdate update, CancellationToken cancellationToken = default);
    /// <summary>Pauses or resumes future occurrences. Resume starts a new interval or selects the next cron time, without catch-up.
    /// Returns false if missing, system-owned, or the optional revision changed. Running jobs are unaffected.</summary>
    Task<bool> SetRecurringEnabled(string id, bool enabled, long? expectedRevision = null, CancellationToken cancellationToken = default);
    /// <summary>Changes whether unfinished occurrences are coalesced across hosts without moving the next run or changing its enabled state.
    /// Returns false if missing, system-owned, or the optional revision changed. Existing outstanding executions are preserved.</summary>
    Task<bool> SetRecurringCoalescing(string id, bool coalesceOccurrences, long? expectedRevision = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This store does not support recurring coalescing.");
    /// <summary>Deletes a recurring definition while preserving executions. Returns false if missing, system-owned, or the optional revision changed.</summary>
    Task<bool> DeleteRecurring(string id, long? expectedRevision = null, CancellationToken cancellationToken = default);
    /// <summary>Reads a manual definition, including its payload, or null when missing.</summary>
    Task<EnqueueRequest?> GetManualDefinition(string id, CancellationToken cancellationToken = default);
    /// <summary>Replaces an existing manual definition's payload and policy; returns false if missing or system-owned.</summary>
    Task<bool> UpdateManual(string id, EnqueueRequest request, CancellationToken cancellationToken = default);
    /// <summary>Deletes a manual definition while preserving executions; returns false if missing or system-owned.</summary>
    Task<bool> DeleteManual(string id, CancellationToken cancellationToken = default);
}
