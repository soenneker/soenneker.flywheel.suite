using System.Diagnostics.Metrics;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal static class FlywheelStorageMetrics
{
    private static readonly Meter Meter = new("Soenneker.Flywheel.Storage");
    internal static readonly Counter<long> Conflicts = Meter.CreateCounter<long>("flywheel.storage.conflicts", description: "Optimistic transactions retried after a conflicting write.");
    internal static readonly Counter<long> Timeouts = Meter.CreateCounter<long>("flywheel.storage.timeouts", description: "Storage operations that exceeded their deadline.");
}
