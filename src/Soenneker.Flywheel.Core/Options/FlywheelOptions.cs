namespace Soenneker.Flywheel.Core.Options;

/// <summary>Runtime concurrency and recovery intervals. Notifications are wake-up hints; eligibility lives in storage.</summary>
public sealed class FlywheelOptions
{
    /// <summary>Exact hosting application build identity used for version-restricted jobs. Defaults to the entry
    /// assembly's module version ID, shared by instances of the same compiled artifact. Override with an immutable
    /// release ID when producers and runners have different entry assemblies. Never use a slot or instance ID.</summary>
    public string ApplicationVersion { get; set; } = System.Reflection.Assembly.GetEntryAssembly()?.ManifestModule.ModuleVersionId.ToString("N") ?? "";
    /// <summary>Number of concurrent worker loops on this host.</summary>
    public int Workers { get; set; } = 4;
    /// <summary>Duration of a job lease before it must be renewed.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum delay before workers retry storage when a change notification is missed or unavailable.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Delay between maintenance and node-heartbeat passes.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Unique identifier of the host owning leases and sending heartbeats.</summary>
    public string NodeId { get; set; } = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    public void Validate()
    {
        if (Workers is < 1 or > 256 || LeaseDuration < TimeSpan.FromSeconds(3) || LeaseDuration > TimeSpan.FromHours(1) ||
            PollInterval < TimeSpan.FromMilliseconds(10) || PollInterval > TimeSpan.FromMinutes(5) ||
            MaintenanceInterval < TimeSpan.FromSeconds(1) || MaintenanceInterval > TimeSpan.FromMinutes(5) || string.IsNullOrWhiteSpace(NodeId) ||
            NodeId.Length > 200 || string.IsNullOrWhiteSpace(ApplicationVersion) || ApplicationVersion.Length > 200)
            throw new ArgumentOutOfRangeException(nameof(FlywheelOptions));
    }
}
