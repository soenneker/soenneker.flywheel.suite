namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>Current public state of a subscribed job.</summary>
public sealed record LiveJob(int Version, string JobId, JobView? Job);
