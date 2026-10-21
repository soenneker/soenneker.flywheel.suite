using Soenneker.Flywheel.Communication.Requests;
namespace Soenneker.Flywheel.Communication.Responses;

/// <summary>An authenticated editor's recurring definition, including its payload and concurrency revision.</summary>
public sealed record ScheduleDefinitionView(string Id, ScheduleUpdate Definition);
