using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Requests;

/// <summary>Credentials submitted to the dashboard sign-in endpoint.</summary>
public sealed record LoginRequest(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password);
