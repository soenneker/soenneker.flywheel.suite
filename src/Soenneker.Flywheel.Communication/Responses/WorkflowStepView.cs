using Soenneker.Flywheel.Communication.Enums;
namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A workflow execution without payloads or lease credentials.</summary>
public sealed record WorkflowStepView(string Id, string Name, string State, string[] PredecessorIds,
    ContinuationCondition Condition, bool Skipped, double? Progress, string? Error);
