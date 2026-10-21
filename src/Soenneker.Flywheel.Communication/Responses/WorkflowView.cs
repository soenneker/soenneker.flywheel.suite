namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A retained workflow with aggregate status and its dependency graph.</summary>
public sealed record WorkflowView(string Id, string State, int Completed, int Total, IReadOnlyList<WorkflowStepView> Steps);
