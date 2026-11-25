using System.Diagnostics.Metrics;

namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal static class FlywheelStorageMetrics
{
    private static readonly Meter Meter = new("Soenneker.Flywheel.Storage");
    internal static readonly Histogram<double> GateWait = Meter.CreateHistogram<double>("flywheel.storage.gate_wait", "ms");
    internal static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("flywheel.storage.transaction", "ms");
    internal static readonly Histogram<double> LeaseHeadroom = Meter.CreateHistogram<double>("flywheel.lease.headroom", "ms");
    internal static readonly Histogram<double> HeartbeatAge = Meter.CreateHistogram<double>("flywheel.heartbeat.age", "ms");
    internal static readonly Histogram<double> ScheduleLag = Meter.CreateHistogram<double>("flywheel.schedule.lag", "ms");
    internal static readonly Counter<long> Rejections = Meter.CreateCounter<long>("flywheel.storage.rejections");
    internal static readonly Counter<long> DroppedLogs = Meter.CreateCounter<long>("flywheel.logs.dropped");
    internal static readonly Counter<long> FailedFlushes = Meter.CreateCounter<long>("flywheel.logs.flush_failures");
    internal static readonly Counter<long> Conflicts = Meter.CreateCounter<long>("flywheel.storage.conflicts", description: "Optimistic transactions retried after a conflicting write.");
    internal static readonly Counter<long> Timeouts = Meter.CreateCounter<long>("flywheel.storage.timeouts", description: "Storage operations that exceeded their deadline.");
}
