namespace Soenneker.Flywheel.Communication.Dtos;

/// <summary>A committed job, schedule or log change; Resync invalidates all subscribed snapshots.</summary>
public sealed record JobChange(string Kind, string? JobId = null)
{
    /// <summary>Requests fresh snapshots after notifications may have been lost, such as a reconnect.</summary>
    public static readonly JobChange Resync = new("Resync");
}
