using Microsoft.JSInterop;
using Soenneker.Blazor.Utils.LocalStorage.Abstract;
using System.Text.Json.Serialization.Metadata;

namespace Soenneker.Flywheel.Dashboard.Tests;

public sealed class DashboardLogOptionsTests
{
    [Test]
    public async Task FormattingPreferenceSurvivesReload()
    {
        var browser = new Browser();
        var options = new DashboardLogOptions(browser);
        await options.Initialize();
        Check(options.FormatExceptions, "Formatting should default to enabled.");
        Check(await options.SetFormatExceptions(false), "Saving should succeed.");
        var restored = new DashboardLogOptions(browser);
        await restored.Initialize();
        Check(!restored.FormatExceptions, "Disabled preference was not restored.");
        await restored.SetFormatExceptions(true);
        var enabled = new DashboardLogOptions(browser);
        await enabled.Initialize();
        Check(enabled.FormatExceptions, "Enabled preference was not restored.");
    }

    [Test]
    public async Task UnavailableStorageKeepsSessionPreference()
    {
        var options = new DashboardLogOptions(new Browser { Unavailable = true });
        await options.Initialize();
        Check(options.FormatExceptions, "Unavailable storage should preserve the default.");
        Check(!await options.SetFormatExceptions(false), "Unavailable storage should report failure.");
        Check(!options.FormatExceptions, "Preference should still apply for this session.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Browser : ILocalStorageUtil
    {
        private readonly Dictionary<string, string> _values = [];
        public bool Unavailable { get; init; }
        public ValueTask Initialize(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<string?> Get(string key, CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new JSException("Storage blocked");
            return ValueTask.FromResult(_values.GetValueOrDefault(key));
        }
        public ValueTask Set(string key, string value, CancellationToken cancellationToken = default)
        {
            if (Unavailable) throw new JSException("Storage blocked");
            _values[key] = value;
            return ValueTask.CompletedTask;
        }
        public ValueTask<T?> Get<T>(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<T?> Get<T>(string key, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask Set<T>(string key, T value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask Set<T>(string key, T value, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask Remove(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask Clear(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> ContainsKey(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> GetKeys(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<int> GetLength(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
