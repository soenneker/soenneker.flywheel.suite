using Microsoft.Extensions.Logging;

namespace Soenneker.Flywheel.Core.Options;

/// <summary>Runtime concurrency and recovery intervals. Notifications are wake-up hints; eligibility lives in storage.</summary>
public sealed class FlywheelOptions
{
    /// <summary>Minimum level of handler ILogger messages captured in job execution logs. Defaults to Information.
    /// Application logging filters must also allow the desired level. None disables handler log capture.</summary>
    public LogLevel MinimumJobLogLevel { get; set; } = LogLevel.Information;
    /// <summary>Exact hosting application build identity used for version-restricted jobs. Defaults to the entry
    /// assembly's module version ID, shared by instances of the same compiled artifact. Override with an immutable
    /// release ID when producers and runners have different entry assemblies. Never use a slot or instance ID.</summary>
    public string ApplicationVersion { get; set; } = System.Reflection.Assembly.GetEntryAssembly()?.ManifestModule.ModuleVersionId.ToString("N") ?? "";
    /// <summary>Isolates the built-in persistent providers by exact ApplicationVersion, including jobs, schedules,
    /// deduplication, method limits and dashboard data. Stop old producers and recurring schedules, then drain
    /// their namespace before removing old workers. Work does not migrate between releases.
    /// Producers and workers must use the same immutable release ID. Disabled by default for existing deployments.</summary>
    public bool IsolateApplicationVersion { get; set; }
    /// <summary>Time allowed for running handlers to finish before shutdown requests their cancellation.</summary>
    public TimeSpan ShutdownGracePeriod { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Time allowed to observe cancellation before an execution is quarantined and the host is stopped.
    /// Managed code cannot be forcibly interrupted; a quarantined execution is never automatically retried.</summary>
    public TimeSpan CancellationGracePeriod { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Maximum time spent in one storage attempt, excluding its final atomic commit. Null uses
    /// one sixth of LeaseDuration. Must be less than the renewal interval so reads yield to lease renewal.</summary>
    public TimeSpan? StorageOperationTimeout { get; set; }
    /// <summary>Returns the configured storage attempt deadline or the lease-derived default.</summary>
    public TimeSpan GetStorageOperationTimeout() => StorageOperationTimeout ?? LeaseDuration / 6;
    /// <summary>Returns the storage namespace (or filesystem path) for this release. The suffix uses a SHA-256
    /// digest so arbitrary immutable release IDs cannot introduce path or namespace delimiters.</summary>
    public string GetStorageName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return IsolateApplicationVersion
            ? name + ".release-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ApplicationVersion)))
            : name;
    }
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
        if (!Enum.IsDefined(MinimumJobLogLevel))
            throw new ArgumentOutOfRangeException(nameof(MinimumJobLogLevel));

        if (Workers is < 1 or > 256 || LeaseDuration < TimeSpan.FromSeconds(3) || LeaseDuration > TimeSpan.FromHours(1) ||
            PollInterval < TimeSpan.FromMilliseconds(10) || PollInterval > TimeSpan.FromMinutes(5) ||
            MaintenanceInterval < TimeSpan.FromSeconds(1) || MaintenanceInterval > TimeSpan.FromMinutes(5) || string.IsNullOrWhiteSpace(NodeId) ||
            NodeId.Length > 200 || string.IsNullOrWhiteSpace(ApplicationVersion) || ApplicationVersion.Length > 200 ||
            ShutdownGracePeriod < TimeSpan.Zero || ShutdownGracePeriod > TimeSpan.FromMinutes(5) ||
            CancellationGracePeriod < TimeSpan.FromMilliseconds(10) || CancellationGracePeriod > TimeSpan.FromMinutes(1) ||
            GetStorageOperationTimeout() < TimeSpan.FromMilliseconds(10) || GetStorageOperationTimeout() >= LeaseDuration / 3)
            throw new ArgumentOutOfRangeException(nameof(FlywheelOptions));
    }
}
