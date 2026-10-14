namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>A registered manual job and its latest execution, without its payload.</summary>
public sealed record ManualJobView(string Id, string Name, string? Description, string? LastExecutionId, string? LastExecutionStatus);
