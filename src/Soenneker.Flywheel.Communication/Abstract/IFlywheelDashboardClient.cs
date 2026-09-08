using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Communication.Abstract;

/// <summary>Typed server-to-dashboard snapshot messages.</summary>
public interface IFlywheelDashboardClient
{
    /// <summary>Delivers the current board query and optional summary.</summary>
    Task BoardSnapshot(LiveBoard snapshot);
    /// <summary>Delivers the current public execution state.</summary>
    Task JobSnapshot(LiveJob snapshot);
    /// <summary>Delivers the retained execution log tail.</summary>
    Task LogSnapshot(LiveLogs snapshot);
}
