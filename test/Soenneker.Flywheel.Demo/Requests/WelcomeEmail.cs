using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Demo.Requests;

/// <param name="Name">Name of the recipient receiving the demo notification.</param>
/// <param name="Address">Recipient email address used by the demo.</param>
public sealed record WelcomeEmail(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("address")] string Address);
