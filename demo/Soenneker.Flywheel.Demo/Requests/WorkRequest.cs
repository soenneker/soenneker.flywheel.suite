namespace Soenneker.Flywheel.Demo.Requests;

/// <param name="Seconds">Requested work duration in seconds.</param>
public sealed record WorkRequest(int Seconds);
