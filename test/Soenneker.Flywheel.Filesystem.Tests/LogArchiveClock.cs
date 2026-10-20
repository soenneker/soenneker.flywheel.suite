namespace Soenneker.Flywheel.Filesystem.Tests;

internal sealed class LogArchiveClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}
