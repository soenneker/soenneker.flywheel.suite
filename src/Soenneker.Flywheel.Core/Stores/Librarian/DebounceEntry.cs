namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record DebounceEntry(string? RequestId = null, long RequestedAtTicks = 0, string? JobId = null,
    string? CommitToken = null, long CommitUntil = 0);
