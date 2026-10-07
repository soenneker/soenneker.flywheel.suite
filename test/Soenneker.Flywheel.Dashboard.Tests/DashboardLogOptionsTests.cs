namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardLogOptionsTests
{
    [Test]
    public async Task FormattingPreferenceSurvivesReload()
    {
        var browser = new TestPreferenceDatabase();
        var options = new DashboardLogOptions(browser);
        await options.Initialize();
        Check(options.FormatExceptions, "Formatting should default to enabled.");
        Check(await options.SetFormatExceptions(false), "Saving should succeed.");
        var restoredDatabase = browser.Reopen();
        var restored = new DashboardLogOptions(restoredDatabase);
        await restored.Initialize();
        Check(!restored.FormatExceptions, "Disabled preference was not restored.");
        await restored.SetFormatExceptions(true);
        var enabled = new DashboardLogOptions(restoredDatabase.Reopen());
        await enabled.Initialize();
        Check(enabled.FormatExceptions, "Enabled preference was not restored.");
    }

    [Test]
    public async Task UnavailableStorageKeepsSessionPreference()
    {
        var options = new DashboardLogOptions(new TestPreferenceDatabase { Unavailable = true });
        await options.Initialize();
        Check(options.FormatExceptions, "Unavailable storage should preserve the default.");
        Check(!await options.SetFormatExceptions(false), "Unavailable storage should report failure.");
        Check(!options.FormatExceptions, "Preference should still apply for this session.");
    }

    [Test]
    public async Task FailedSaveKeepsSessionPreferenceWithoutPersistingIt()
    {
        var database = new TestPreferenceDatabase { SaveUnavailable = true };
        var options = new DashboardLogOptions(database);
        Check(!await options.SetFormatExceptions(false), "A failed flush should report failure.");
        Check(!options.FormatExceptions, "The session preference should still apply.");
        var restored = new DashboardLogOptions(database.Reopen());
        await restored.Initialize();
        Check(restored.FormatExceptions, "An unsaved preference must not survive reopening the database.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

}
