using Soenneker.Blazor.ApiClient.Abstract;

namespace Soenneker.Flywheel.Dashboard.Communication.Abstract;

/// <summary>Cookie-authenticated Flywheel transport. Mutating requests acquire and attach an antiforgery token.</summary>
public interface IFlywheelApiClient : IApiClient
{
    /// <summary>Backend application base address, independent of the dashboard's navigation route.</summary>
    Uri BaseAddress { get; }
}
