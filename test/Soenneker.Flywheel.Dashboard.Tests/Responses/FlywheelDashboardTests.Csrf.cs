namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed partial class FlywheelDashboardTests
{
    /// <param name="Token">Antiforgery token submitted with authenticated dashboard mutations.</param>
    private sealed record Csrf(string Token);
}
