namespace Soenneker.Flywheel.Communication.Requests;

/// <summary>Credentials submitted to the dashboard sign-in endpoint.</summary>
public sealed record LoginRequest(string? Username, string? Password);
