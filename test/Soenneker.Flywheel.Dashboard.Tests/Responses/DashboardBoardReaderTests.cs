using System.Text.Json;
using Soenneker.Flywheel.Communication.Responses;
using Soenneker.Flywheel.Dashboard.Communication;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardBoardReaderTests
{
    [Test]
    public async Task BatchedReaderPreservesTheTypedSignalRPayload()
    {
        using var document = JsonDocument.Parse("""
        {
          "version": 7, "totalCount": 50,
          "items": [{ "id": "one", "name": "demo.job", "state": "Running", "attempt": 2,
            "updatedAt": 1789841911000, "cancelRequested": true, "error": "details", "version": 8,
            "createdAt": 1789841900000, "dueAt": 1789841901000, "leaseUntil": 1789841999000,
            "owner": "worker", "parentJobId": "parent", "nextJobId": "next", "maxAttempts": 5,
            "timeoutSeconds": 15.5, "priority": "high", "progress": 43.2, "progressMessage": "working",
            "progressUpdatedAt": 1789841910000, "description": "Demo", "startedAt": 1789841902000,
            "completedAt": 0 }],
          "history": [{ "timestamp": 60000, "scheduled": 2, "running": 1, "succeeded": 3,
            "deadLettered": 4, "cancelled": 5, "waiting": 6, "queued": 7 }],
          "liveActivity": [{ "timestamp": 61000, "scheduled": 2, "running": 1, "succeeded": 3,
            "deadLettered": 0, "runningCount": 4, "scheduledCount": 5, "queuedCount": 6 }],
          "schedules": { "recurring": [], "scheduled": [] },
          "runningCount": 4, "serverCount": 2, "totalWorkers": 8, "recurringCount": 3,
          "failedCount": 12, "succeededCount": 9876543210
        }
        """);
        var expected = document.RootElement.Deserialize<LiveBoard>(JsonSerializerOptions.Web)!;
        var actual = await DashboardBoardReader.Read(document.RootElement, CancellationToken.None);
        if (JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(expected))
            throw new Exception("Batched decoding changed the live snapshot contract.");
    }

    [Test]
    public async Task OptionalSummariesStayNullAndCancellationDoesNotPublishAPartialBoard()
    {
        using var document = JsonDocument.Parse("""{"version":1,"items":[],"totalCount":0}""");
        var board = await DashboardBoardReader.Read(document.RootElement, CancellationToken.None);
        if (board.History is not null || board.LiveActivity is not null || board.Schedules is not null || board.RunningCount is not null ||
            board.FailedCount is not null || board.SucceededCount is not null)
            throw new Exception("Missing optional summaries were invented.");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        try
        {
            await DashboardBoardReader.Read(document.RootElement, cancellation.Token);
            throw new Exception("A cancelled subscription received a board.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    [Test]
    public async Task RetainedTotalsRefreshFromTheWireIncludingZeroAndNull()
    {
        var totals = new ActivityTotalsState();
        foreach (long? count in new long?[] { 42, 0, null })
        {
            var expected = new LiveBoard(1, [], 0, null, null, FailedCount: count, SucceededCount: count);
            JsonElement element = JsonSerializer.SerializeToElement(expected);
            totals.UpdateSnapshot(await DashboardBoardReader.Read(element, CancellationToken.None));
            if (totals.FailedCount != count || totals.SucceededCount != count)
                throw new Exception("Retained totals did not reach the header state from the live payload.");
        }
    }

    [Test]
    public async Task LargeSnapshotsCanBeCancelledBetweenBatches()
    {
        var point = new JobHistoryPoint(60000, 1, 2, 3, 4);
        var board = new LiveBoard(1, [], 0, Enumerable.Repeat(point, 10000).ToList(), null);
        JsonElement element = JsonSerializer.SerializeToElement(board);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        try
        {
            await DashboardBoardReader.Read(element, cancellation.Token);
            throw new Exception("A cancelled large snapshot was published.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
}
