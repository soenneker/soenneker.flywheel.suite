using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Communication.Responses;

/// <param name="Token">Antiforgery token submitted with authenticated dashboard mutations.</param>
public sealed record Csrf(
    [property: JsonPropertyName("token")] string Token);
