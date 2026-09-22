using System.Globalization;
using Microsoft.JSInterop;
using Soenneker.Blazor.Utils.LocalStorage.Abstract;

namespace Soenneker.Flywheel.Dashboard;

/// <summary>Browser-persisted timezone used for dashboard timestamps and calendar ranges.</summary>
public sealed class DashboardTimeZone(ILocalStorageUtil storage)
{
    private TimeZoneInfo _selectedZone = TimeZoneInfo.Utc;
    public bool Enabled { get; private set; } = true;
    public string SelectedId => _selectedZone.Id;
    public string PreferenceKey => $"{Enabled}:{SelectedId}";
    public TimeZoneInfo Zone => Enabled ? _selectedZone : TimeZoneInfo.Utc;
    public string Id => Zone.Id;
    public IReadOnlyList<TimeZoneInfo> Zones { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Append(TimeZoneInfo.Utc).DistinctBy(zone => zone.Id).OrderBy(zone => zone.Id).ToArray();
    public event Action? Changed;

    public async Task Initialize()
    {
        string id = TimeZoneInfo.Local.Id;
        try
        {
            id = await storage.Get("flywheel.timezone") is { Length: > 0 } saved ? saved : id;
            Enabled = await storage.Get("flywheel.timezone.enabled") != "false";
        }
        catch (JSException) { }
        try { _selectedZone = TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }
    }

    public async Task<bool> Select(string id)
    {
        _selectedZone = TimeZoneInfo.FindSystemTimeZoneById(id);
        Changed?.Invoke();
        return await Save("flywheel.timezone", id);
    }

    /// <summary>Updates and persists whether dashboard timezone conversion is enabled.</summary>
    public async Task<bool> SetEnabled(bool enabled)
    {
        Enabled = enabled;
        Changed?.Invoke();
        return await Save("flywheel.timezone.enabled", enabled ? "true" : "false");
    }

    private async Task<bool> Save(string key, string value)
    {
        try
        {
            await storage.Set(key, value);
            return true;
        }
        catch (JSException) { return false; }
    }

    public string FormatMessage(string message) => Enabled ? LogMessageTimestamps.Convert(message, Zone) : message;

    public DateTimeOffset Convert(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Zone);
    public string Format(DateTimeOffset value, string format = "yyyy-MM-dd HH:mm:ss")
    {
        DateTimeOffset local = Convert(value);
        string timestamp = local.ToString(format, CultureInfo.InvariantCulture);
        return $"{timestamp} {ZoneLabel}";
    }
    /// <summary>Short display label for the selected zone, independent of daylight-saving time.</summary>
    public string ZoneLabel
    {
        get
        {
            if (Zone.Equals(TimeZoneInfo.Utc)) return "UTC";
            string windowsId = TimeZoneInfo.TryConvertIanaIdToWindowsId(Id, out string? mapped) ? mapped : Id;
            return windowsId switch
            {
                "Eastern Standard Time" or "US Eastern Standard Time" or "Eastern Standard Time (Mexico)" => "ET",
                "Central Standard Time" or "Central Standard Time (Mexico)" or "Canada Central Standard Time" => "CT",
                "Mountain Standard Time" or "US Mountain Standard Time" or "Mountain Standard Time (Mexico)" => "MT",
                "Pacific Standard Time" or "Pacific Standard Time (Mexico)" => "PT",
                "Alaskan Standard Time" => "AKT",
                "Hawaiian Standard Time" => "HT",
                "Atlantic Standard Time" => "AT",
                "Newfoundland Standard Time" => "NT",
                "India Standard Time" => "IST",
                "Nepal Standard Time" => "NPT",
                "Tokyo Standard Time" => "JST",
                "Korea Standard Time" => "KST",
                "China Standard Time" => "CST",
                "Singapore Standard Time" => "SGT",
                _ => Id
            };
        }
    }

    public string Format(long value, string format = "yyyy-MM-dd HH:mm:ss") => Format(DateTimeOffset.FromUnixTimeMilliseconds(value), format);
    public DateOnly Today => DateOnly.FromDateTime(Convert(DateTimeOffset.UtcNow).DateTime);

    public DateTimeOffset StartOfDay(DateOnly date)
    {
        DateTime local = date.ToDateTime(TimeOnly.MinValue);
        // Some zones advance clocks at midnight, or skip an entire calendar day.
        while (Zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Zone));
    }

    public string[] ChartLabels(IEnumerable<double> timestamps, bool live) => timestamps
        .Select(value => Format((long)value, live ? "HH:mm:ss" : "MMM d HH:mm")).ToArray();
}
