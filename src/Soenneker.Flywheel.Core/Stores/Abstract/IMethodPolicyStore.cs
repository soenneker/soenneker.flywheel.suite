using Soenneker.Flywheel.Communication.Dtos;

namespace Soenneker.Flywheel.Core.Stores.Abstract;

/// <summary>Durable runtime configuration of method-wide dispatch limits.</summary>
public interface IMethodPolicyStore
{
    /// <summary>Replaces limits for subsequent claims, including queued jobs. Existing executions continue.
    /// An unchanged policy preserves throttle usage; a changed policy resets it.</summary>
    Task ConfigureMethod(string name, MethodPolicy policy, CancellationToken cancellationToken = default);
}
