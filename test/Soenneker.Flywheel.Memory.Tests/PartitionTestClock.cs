namespace Soenneker.Flywheel.Memory.Tests;

internal sealed class PartitionTestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}
