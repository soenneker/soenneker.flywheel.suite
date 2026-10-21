namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A persisted workflow and its execution IDs in submission order, including the optional final step.</summary>
public sealed record WorkflowSubmission(string Id, IReadOnlyList<string> JobIds, string? ContinuationId = null);
