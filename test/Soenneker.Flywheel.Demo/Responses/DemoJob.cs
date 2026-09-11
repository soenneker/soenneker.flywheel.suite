using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Demo.Responses;

/// <param name="Scenario">Description of the scenario demonstrated by this job.</param>
/// <param name="Id">Identifier of the enqueued demo job.</param>
public sealed record DemoJob(
    [property: JsonPropertyName("scenario")] string Scenario,
    [property: JsonPropertyName("id")] string Id);
