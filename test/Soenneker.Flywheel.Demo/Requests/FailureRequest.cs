namespace Soenneker.Flywheel.Demo.Requests;

/// <param name="Reason">Reason included in the simulated failure.</param>
public sealed record FailureRequest(string Reason);
