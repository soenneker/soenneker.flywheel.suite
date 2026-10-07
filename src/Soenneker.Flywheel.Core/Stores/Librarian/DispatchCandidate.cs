namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal sealed record DispatchCandidate(string Id, string Name, string? ApplicationVersion, int Priority, long DueAt, string? TargetNodeId = null,
    string? PartitionId = null)
{
    // Fixed-width fields preserve descending priority, signed timestamp order, then ordinal job ID.
    public string Order => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{int.MaxValue - Priority:D10}:{(unchecked((ulong)DueAt) ^ 0x8000000000000000UL):D20}:{Id}");
}
