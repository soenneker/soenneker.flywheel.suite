namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record WorkflowRecord(string Id, string[] JobIds, string[] SubmissionKeys);
