namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardTimeZoneTests
{
    [Test]
    public async ValueTask SelectedZoneSurvivesReloadAndHandlesDaylightSaving()
    {
        var browser = new TestPreferenceDatabase();
        var zone = new DashboardTimeZone(browser);
        await zone.Select("America/Chicago");
        var reloaded = new DashboardTimeZone(browser.Reopen());
        await reloaded.Initialize();
        Check(reloaded.Id == "America/Chicago", "Saved timezone was not restored.");
        Check(reloaded.Format(DateTimeOffset.Parse("2026-01-01T02:00:00Z")) == "2025-12-31 20:00:00 CT", "Winter conversion or date rollover failed.");
        Check(reloaded.Format(DateTimeOffset.Parse("2026-07-01T02:00:00Z")) == "2026-06-30 21:00:00 CT", "Summer conversion failed.");
        Check((reloaded.StartOfDay(new(2026, 3, 9)) - reloaded.StartOfDay(new(2026, 3, 8))).TotalHours == 23, "Spring calendar range must span 23 hours.");
        Check((reloaded.StartOfDay(new(2026, 11, 2)) - reloaded.StartOfDay(new(2026, 11, 1))).TotalHours == 25, "Fall calendar range must span 25 hours.");
    }

    [Test]
    public async ValueTask InvalidSavedZoneFallsBackToUtcAndFractionalOffsetsArePreserved()
    {
        var browser = new TestPreferenceDatabase();
        await browser.SetPreference("flywheel.timezone", "invalid/timezone");
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
    public async ValueTask ConversionTogglePersistsAndRetainsSelectedTimezone()
    {
        var browser = new TestPreferenceDatabase();
        var zone = new DashboardTimeZone(browser);
        await zone.Select("America/Chicago");
        await zone.SetEnabled(false);
        var restoredDatabase = browser.Reopen();
        var restored = new DashboardTimeZone(restoredDatabase);
        await restored.Initialize();
        Check(!restored.Enabled && restored.SelectedId == "America/Chicago" && restored.Id == "UTC", "Disabled preference or selected timezone was not restored.");
        const string message = "Due 2026-07-01T02:00:00Z";
        Check(restored.FormatMessage(message) == message, "Disabled conversion modified the log message.");
        Check(restored.Format(DateTimeOffset.Parse("2026-07-01T02:00:00Z")) == "2026-07-01 02:00:00 UTC", "Disabled timestamps must use UTC.");
        await restored.SetEnabled(true);
        Check(restored.FormatMessage(message) == "Due 2026-06-30T21:00:00-05:00", "Re-enabling did not restore the selected timezone.");
        Check(await restoredDatabase.Reopen().GetPreference("flywheel.timezone.enabled") == "true", "Enabled preference was not saved.");
    }

    [Test]
    [Arguments("America/New_York", "ET")]
    [Arguments("America/Chicago", "CT")]
    [Arguments("America/Denver", "MT")]
    [Arguments("America/Los_Angeles", "PT")]
    [Arguments("America/Toronto", "ET")]
    [Arguments("America/Phoenix", "MT")]
    public async ValueTask DisplayUsesRegionalAbbreviations(string id, string label)
    {
        var zone = new DashboardTimeZone(new TestPreferenceDatabase());
        await zone.Select(id);
        Check(zone.ZoneLabel == label, "Unexpected regional abbreviation.");
        Check(zone.Format(DateTimeOffset.Parse("2026-07-01T00:00:00Z")).EndsWith(" " + label), "Timestamp did not use the abbreviation.");
    }

    [Test]
    public async ValueTask BlockedStorageStillAllowsSessionPreferences()
    {
        var zone = new DashboardTimeZone(new TestPreferenceDatabase { Unavailable = true });
        await zone.Initialize();
        Check(!await zone.Select("America/Chicago"), "Blocked storage should report unsaved preference.");
        Check(zone.SelectedId == "America/Chicago", "Session preference was lost.");
        Check(!await zone.SetEnabled(false) && !zone.Enabled, "Session toggle should apply when storage is blocked.");
    }

    [Test]
    public async Task FailedSaveKeepsSessionTimezoneWithoutPersistingIt()
    {
        var database = new TestPreferenceDatabase { SaveUnavailable = true };
        var zone = new DashboardTimeZone(database);
        Check(!await zone.Select("America/Chicago"), "A failed flush should report failure.");
        Check(zone.SelectedId == "America/Chicago", "The session timezone should still apply.");
        Check(!await zone.SetEnabled(false) && !zone.Enabled, "The session toggle should apply despite a failed flush.");
        var restored = new DashboardTimeZone(database.Reopen());
        await restored.Initialize();
        Check(restored.Id == "UTC" && restored.Enabled, "Unsaved preferences must not survive reopening the database.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

}
