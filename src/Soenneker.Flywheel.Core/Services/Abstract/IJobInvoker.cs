namespace Soenneker.Flywheel.Core.Services.Abstract;

using Soenneker.Flywheel.Communication.Dtos;

/// <summary>Compile-time generated invocation adapter resolved inside an execution scope.</summary>
public interface IJobInvoker
{
    /// <summary>Stable persisted job name; changing it requires migrating outstanding jobs.</summary>
    string Name { get; }
    /// <summary>Generated distributed execution limits declared on the job method, or null for defaults.</summary>
    MethodPolicy? Policy => null;
    /// <summary>Invokes the DI handler without reflection. Observe cancellation and make side effects idempotent.</summary>
    Task Invoke(IServiceProvider services, string payload, CancellationToken cancellationToken);
}
