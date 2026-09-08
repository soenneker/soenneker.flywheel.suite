namespace Soenneker.Flywheel.Communication.Abstract;

/// <summary>Server operations available to an authenticated live dashboard connection.</summary>
public interface IFlywheelDashboardHub
{
    /// <summary>Subscribes to a bounded search page and optional summary.</summary>
    Task SubscribeBoard(int version, string? query, int offset, int count, bool summary, DateTimeOffset? startAt = null, DateTimeOffset? endAt = null);
    /// <summary>Subscribes to one execution.</summary>
    Task SubscribeJob(int version, string jobId);
    /// <summary>Subscribes to the retained log tail for one execution.</summary>
    Task SubscribeLogs(int version, string jobId);
}
