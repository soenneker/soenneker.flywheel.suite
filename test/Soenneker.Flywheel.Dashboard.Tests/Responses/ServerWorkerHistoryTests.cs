using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class ServerWorkerHistoryTests
{
    [Test]
    public void FreshPageLoadsStoredHistoryIncludingTimeAway()
    {
        long now = 1800000000000;
        var server = new ServerView("server", now + 30000, 8, [])
        {
            ObservedAt = now,
            WorkerHistory = [new(now - 20000, 2, now + 10000), new(now - 10000, 4, now + 20000)]
        };
        var firstVisit = new ServerWorkerHistory();
        firstVisit.Update(server);
        var returned = new ServerWorkerHistory();
        returned.Update(server);
        if (returned.Data.Count != 61 || !returned.Data.Series[0].Values.SequenceEqual(firstVisit.Data.Series[0].Values)
            || returned.Data.Series[0].Values[^5] != 2 || returned.Data.Series[0].Values[^1] != 4)
            throw new Exception("A fresh page must load stored observations without a session cache");
        if (returned.Data.Series[0].Values.Take(56).Any(value => value is not null))
            throw new Exception("Times before the first heartbeat must remain unknown");
    }

    [Test]
    public void ExpiredHeartbeatsLeaveGapsAndWindowRemainsFixed()
    {
        long now = 1800000000000;
        var history = new ServerWorkerHistory();
        history.Update(new ServerView("server", now + 30000, 8, [])
        {
            ObservedAt = now,
            WorkerHistory = [new(now - 20000, 2, now - 10000), new(now, 1, now + 30000)]
        });
        if (history.Data.Series[0].Values[^3] is not null || history.Data.Series[0].Values[^2] is not null
            || history.Data.Series[0].Values[^1] != 1 || history.Data.XValues[^1] - history.Data.XValues[0] != 300000)
            throw new Exception("Expired heartbeats must leave gaps in the fixed five-minute window");
    }
}
