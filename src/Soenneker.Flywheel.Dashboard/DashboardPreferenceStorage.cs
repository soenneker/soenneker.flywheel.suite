using System.Text.Json;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Flywheel.Dashboard;

internal static class DashboardPreferenceStorage
{
    internal const string ServiceKey = "Flywheel.Dashboard.Preferences";
    internal const string ContainerName = "flywheel.dashboard.preferences";

    internal static async ValueTask<string?> GetPreference(this ILibrarianDatabase database, string key)
    {
        ILibrarianContainer container = await database.GetContainer(ContainerName);
        string? value = await container.GetItem(key);
        return value is null ? null : JsonSerializer.Deserialize(value, DashboardPreferenceJsonContext.Default.String);
    }

    internal static async ValueTask SetPreference(this ILibrarianDatabase database, string key, string value)
    {
        ILibrarianContainer container = await database.GetContainer(ContainerName);
        string document = JsonSerializer.Serialize(value, DashboardPreferenceJsonContext.Default.String);
        if (await container.UpdateItem(key, document) is null)
            await container.AddItem(key, document);
        await database.Save();
    }
}
