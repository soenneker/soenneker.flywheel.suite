namespace Soenneker.Flywheel.Demo.Responses;

/// <param name="BatchId">Identifier grouping the jobs created by a demo tour.</param>
/// <param name="Jobs">Jobs included in this demo tour.</param>
/// <param name="Deduplicated">Whether the duplicate enqueue returned the original job identifier.</param>
/// <param name="RecurringCreated">Whether the tour created the recurring schedule.</param>
public sealed record DemoRun(string BatchId, IReadOnlyList<DemoJob> Jobs, bool Deduplicated, bool RecurringCreated);
