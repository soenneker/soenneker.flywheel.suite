namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record ManualJobSummary(ManualJobMetadata Job, string? LastExecutionId, string? LastExecutionStatus);
