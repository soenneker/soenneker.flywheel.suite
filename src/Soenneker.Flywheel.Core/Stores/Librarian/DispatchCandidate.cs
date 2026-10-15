namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record DispatchCandidate(string Id, string Name, string? ApplicationVersion, int Priority, long DueAt, string? TargetNodeId = null,
    string? PartitionId = null, long QueueRank = 0)
{
    public int OrderVersion => 2;
    // Operator rank comes first, followed by descending priority, due time, and ordinal job ID.
    public string Order => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{(unchecked((ulong)QueueRank) ^ 0x8000000000000000UL):D20}:{int.MaxValue - Priority:D10}:{(unchecked((ulong)DueAt) ^ 0x8000000000000000UL):D20}:{Id}");
}
