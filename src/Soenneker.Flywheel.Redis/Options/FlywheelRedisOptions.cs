using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Flywheel.Core;
using Soenneker.Redis.Client.Abstract;
using Soenneker.Redis.Client.Registrars;
using StackExchange.Redis;

namespace Soenneker.Flywheel.Redis;

/// <summary>All keys share a Redis Cluster hash slot. Use a dedicated namespace and a persistence-enabled Redis deployment.</summary>
public sealed class FlywheelRedisOptions
{
    /// <summary>Redis connection string used by the storage provider.</summary>
    public string ConnectionString { get; set; } = "localhost:6379";
    /// <summary>Prefix isolating this host's Redis data from other Flywheel deployments.</summary>
    public string Namespace { get; set; } = "default";
    /// <summary>Zero-based Redis database index.</summary>
    public int Database { get; set; }
    /// <summary>Amount of aggregate job activity to retain for dashboard graphs.</summary>
    public TimeSpan HistoryRetention { get; set; } = TimeSpan.FromDays(1);
    /// <summary>Whether terminal job records and their logs are retained for the history retention period.</summary>
    public bool RetainCompletedJobs { get; set; } = true;
}
