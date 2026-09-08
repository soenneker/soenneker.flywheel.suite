using Soenneker.Dtos.Results.Operation;

namespace Soenneker.Flywheel.Dashboard.Communication;

/// <summary>Result helpers for UI operations that use a common failure path.</summary>
internal static class DashboardResultExtensions
{
    public static void EnsureSucceeded<T>(this OperationResult<T> result)
    {
        if (result.Failed || result.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException(result.Problem?.Title ?? "Dashboard operation failed.");
    }
}
