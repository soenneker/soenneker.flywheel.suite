namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record DispatchCandidate(string Id, string Name, string? ApplicationVersion, int Priority, long DueAt, string? TargetNodeId = null);
