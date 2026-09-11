using System.Text.Json.Serialization;

namespace Soenneker.Flywheel.Demo.Requests;

/// <param name="Title">Title of the report to generate.</param>
/// <param name="Sections">Number of report sections to generate.</param>
public sealed record ReportRequest(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("sections")] int Sections);
