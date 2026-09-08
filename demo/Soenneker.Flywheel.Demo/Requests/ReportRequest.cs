namespace Soenneker.Flywheel.Demo.Requests;

/// <param name="Title">Title of the report to generate.</param>
/// <param name="Sections">Number of report sections to generate.</param>
public sealed record ReportRequest(string Title, int Sections);
