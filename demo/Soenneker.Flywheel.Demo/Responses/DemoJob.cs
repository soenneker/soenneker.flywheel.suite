namespace Soenneker.Flywheel.Demo.Responses;

/// <param name="Scenario">Description of the scenario demonstrated by this job.</param>
/// <param name="Id">Identifier of the enqueued demo job.</param>
public sealed record DemoJob(string Scenario, string Id);
