using Microsoft.JSInterop;
using Soenneker.Blazor.Utils.LocalStorage.Abstract;

namespace Soenneker.Flywheel.Dashboard;

/// <summary>Browser-persisted presentation options for execution logs.</summary>
public sealed class DashboardLogOptions(ILocalStorageUtil storage)
{
    private const string StorageKey = "flywheel.logs.formatExceptions";

    /// <summary>Whether exception traces use expandable formatting.</summary>
    public bool FormatExceptions { get; private set; } = true;

    /// <summary>Loads the browser's saved preference.</summary>
    public async Task Initialize()
    {
        try { FormatExceptions = await storage.Get(StorageKey) != "false"; }
        catch (JSException) { }
    }

    /// <summary>Applies the preference and returns whether browser persistence succeeded.</summary>
    public async Task<bool> SetFormatExceptions(bool enabled)
    {
        FormatExceptions = enabled;
        try
        {
            await storage.Set(StorageKey, enabled ? "true" : "false");
            return true;
        }
        catch (JSException) { return false; }
    }
}
