namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record RunningEntry(string Name, long LeaseUntil, string? PartitionId = null);
