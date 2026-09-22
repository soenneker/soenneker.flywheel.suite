using Microsoft.JSInterop;
using Soenneker.Blazor.Utils.LocalStorage.Abstract;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardTimeZoneTests
{
    [Test]
    public async Task SelectedZoneSurvivesReloadAndHandlesDaylightSaving()
    {
        var browser = new Browser();
        var zone = new DashboardTimeZone(browser);
        await zone.Select("America/Chicago");
        var reloaded = new DashboardTimeZone(browser);
        await reloaded.Initialize();
        Check(reloaded.Id == "America/Chicago", "Saved timezone was not restored.");
        Check(reloaded.Format(DateTimeOffset.Parse("2026-01-01T02:00:00Z")) == "2025-12-31 20:00:00 CT", "Winter conversion or date rollover failed.");
        Check(reloaded.Format(DateTimeOffset.Parse("2026-07-01T02:00:00Z")) == "2026-06-30 21:00:00 CT", "Summer conversion failed.");
        Check((reloaded.StartOfDay(new(2026, 3, 9)) - reloaded.StartOfDay(new(2026, 3, 8))).TotalHours == 23, "Spring calendar range must span 23 hours.");
        Check((reloaded.StartOfDay(new(2026, 11, 2)) - reloaded.StartOfDay(new(2026, 11, 1))).TotalHours == 25, "Fall calendar range must span 25 hours.");
    }

    [Test]
    public async Task InvalidSavedZoneFallsBackToUtcAndFractionalOffsetsArePreserved()
    {
        var browser = new Browser { Saved = "invalid/timezone" };
        var zone = new DashboardTimeZone(browser);
        await zone.Initialize();
        Check(zone.Id == "UTC", "Invalid preference should fall back to UTC.");
        await zone.Select("Asia/Kathmandu");
        Check(zone.Format(DateTimeOffset.Parse("2026-01-01T00:00:00Z")) == "2026-01-01 05:45:00 NPT", "Fractional timezone offset was lost.");
    }

    [Test]
    public void EmbeddedTimestampsConvertWithoutChangingJsonOrPrecision()
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        string message = """{"createdAt":"2026-01-01T02:00:00.1234567Z","dueAt":"2026-07-01T02:00:00+02:00"}""";
        string expected = """{"createdAt":"2025-12-31T20:00:00.1234567-06:00","dueAt":"2026-06-30T19:00:00-05:00"}""";
        Check(LogMessageTimestamps.Convert(message, zone) == expected, "JSON timestamps or precision changed incorrectly.");
        Check(LogMessageTimestamps.Convert("Due 2026-07-01 02:00:00 UTC; offset 2026-07-01T02:00:00+0545.", zone)
            == "Due 2026-06-30 21:00:00 UTC-05:00; offset 2026-06-30T15:15:00-05:00.", "Text timestamps did not convert.");
    }

    [Test]
    public void EmbeddedTimestampsLeaveAmbiguousAndInvalidValuesUnchanged()
    {
        const string message = "2026-01-01 02:00:00; 2026-01-01; 1780000000000; 2026-02-30T02:00:00Z; id2026-01-01T02:00:00Z; 2026-01-01T02:00:00+25:00";
        Check(LogMessageTimestamps.Convert(message, TimeZoneInfo.FindSystemTimeZoneById("America/Chicago")) == message,
            "Ambiguous timestamps, invalid dates, or identifiers were rewritten.");
    }

    [Test]
    public void EmbeddedTimestampsRespectDstAndNewTimezoneSelection()
    {
        const string message = "2026-03-08T07:59:59Z → 2026-03-08T08:00:00Z";
        Check(LogMessageTimestamps.Convert(message, TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"))
            == "2026-03-08T01:59:59-06:00 → 2026-03-08T03:00:00-05:00", "DST boundary failed.");
        Check(LogMessageTimestamps.Convert(message, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu"))
            == "2026-03-08T13:44:59+05:45 → 2026-03-08T13:45:00+05:45", "Timezone change failed.");
    }

    [Test]
    public async Task ConversionTogglePersistsAndRetainsSelectedTimezone()
    {
        var browser = new Browser();
        var zone = new DashboardTimeZone(browser);
        await zone.Select("America/Chicago");
        await zone.SetEnabled(false);
        var restored = new DashboardTimeZone(browser);
        await restored.Initialize();
        Check(!restored.Enabled && restored.SelectedId == "America/Chicago" && restored.Id == "UTC", "Disabled preference or selected timezone was not restored.");
        const string message = "Due 2026-07-01T02:00:00Z";
        Check(restored.FormatMessage(message) == message, "Disabled conversion modified the log message.");
        Check(restored.Format(DateTimeOffset.Parse("2026-07-01T02:00:00Z")) == "2026-07-01 02:00:00 UTC", "Disabled timestamps must use UTC.");
        await restored.SetEnabled(true);
        Check(restored.FormatMessage(message) == "Due 2026-06-30T21:00:00-05:00", "Re-enabling did not restore the selected timezone.");
        Check(browser.Enabled, "Enabled preference was not saved.");
    }

    [Test]
    [Arguments("America/New_York", "ET")]
    [Arguments("America/Chicago", "CT")]
    [Arguments("America/Denver", "MT")]
    [Arguments("America/Los_Angeles", "PT")]
    [Arguments("America/Toronto", "ET")]
    [Arguments("America/Phoenix", "MT")]
    public async Task DisplayUsesRegionalAbbreviations(string id, string label)
    {
        var zone = new DashboardTimeZone(new Browser());
        await zone.Select(id);
        Check(zone.ZoneLabel == label, "Unexpected regional abbreviation.");
        Check(zone.Format(DateTimeOffset.Parse("2026-07-01T00:00:00Z")).EndsWith(" " + label), "Timestamp did not use the abbreviation.");
    }

    [Test]
    public async Task BlockedStorageStillAllowsSessionPreferences()
    {
        var zone = new DashboardTimeZone(new Browser { Unavailable = true });
        await zone.Initialize();
        Check(!await zone.Select("America/Chicago"), "Blocked storage should report unsaved preference.");
        Check(zone.SelectedId == "America/Chicago", "Session preference was lost.");
        Check(!await zone.SetEnabled(false) && !zone.Enabled, "Session toggle should apply when storage is blocked.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Browser : ILocalStorageUtil
    {
        public string Saved { get; set; } = "UTC";
        public bool Enabled { get; set; } = true;
        public bool Unavailable { get; set; }
        public ValueTask Initialize(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<string?> Get(string key, CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new JSException("Storage blocked");
            return ValueTask.FromResult<string?>(key == "flywheel.timezone" ? Saved : Enabled ? "true" : "false");
        }
        public ValueTask Set(string key, string value, CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new JSException("Storage blocked");
            if (key == "flywheel.timezone") Saved = value;
            else if (key == "flywheel.timezone.enabled") Enabled = value == "true";
            else throw new InvalidOperationException("Unexpected preference key.");
            return ValueTask.CompletedTask;
        }
        public ValueTask<T?> Get<T>(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask Set<T>(string key, T value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask Remove(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask Clear(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> ContainsKey(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> GetKeys(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<int> GetLength(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
