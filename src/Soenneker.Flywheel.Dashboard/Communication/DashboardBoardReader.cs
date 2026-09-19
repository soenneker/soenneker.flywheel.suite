using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Soenneker.Flywheel.Communication.Responses;

namespace Soenneker.Flywheel.Dashboard.Communication;

internal static class DashboardBoardReader
{
    public static async ValueTask<LiveBoard> Read(JsonElement element, CancellationToken cancellationToken)
    {
        var budget = new ReadBudget(cancellationToken);
        var json = DashboardBoardJsonContext.Default;
        List<JobView> items = await ReadArray(element.GetProperty("items"), json.JobView, budget);
        List<JobHistoryPoint>? history = Property(element, "history") is { } historyElement
            ? await ReadArray(historyElement, json.JobHistoryPoint, budget) : null;
        List<JobHistoryPoint>? activity = Property(element, "liveActivity") is { } activityElement
            ? await ReadArray(activityElement, json.JobHistoryPoint, budget) : null;
        ScheduleView? schedules = null;
        if (Property(element, "schedules") is { } scheduleElement)
        {
            List<RecurringScheduleView> recurring = await ReadArray(scheduleElement.GetProperty("recurring"), json.RecurringScheduleView, budget);
            List<JobView> scheduled = await ReadArray(scheduleElement.GetProperty("scheduled"), json.JobView, budget);
            schedules = new ScheduleView(recurring, scheduled);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new LiveBoard(element.GetProperty("version").GetInt32(), items, element.GetProperty("totalCount").GetInt32(),
            history, schedules, Property(element, "runningCount")?.GetInt64(), Property(element, "serverCount")?.GetInt32(),
            Property(element, "totalWorkers")?.GetInt32(), activity, Property(element, "recurringCount")?.GetInt64());
    }

    private static JsonElement? Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static async ValueTask<List<T>> ReadArray<T>(JsonElement array, JsonTypeInfo<T> type, ReadBudget budget)
    {
        var result = new List<T>(array.GetArrayLength());
        foreach (JsonElement item in array.EnumerateArray())
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            result.Add(item.Deserialize(type) ?? throw new JsonException("A board snapshot entry cannot be null."));
            await budget.YieldIfNeeded();
        }
        return result;
    }

    private sealed class ReadBudget(CancellationToken cancellationToken)
    {
        private long _started = Stopwatch.GetTimestamp();
        public CancellationToken CancellationToken => cancellationToken;

        public async ValueTask YieldIfNeeded()
        {
            if (Stopwatch.GetElapsedTime(_started).TotalMilliseconds < 4) return;
            // Task.Yield can resume in the same WASM work queue. A timer lets the
            // browser paint between batches instead of decoding the whole board
            // in a single UI-thread task. Publish only after every batch succeeds.
            await Task.Delay(1, cancellationToken);
            _started = Stopwatch.GetTimestamp();
        }
    }
}
