using System.Text.Json.Serialization;
namespace Soenneker.Flywheel.Core.Tests.Jobs;
public sealed record JobPayloadTestModel([property: JsonPropertyName("wire_value")] string Value);
