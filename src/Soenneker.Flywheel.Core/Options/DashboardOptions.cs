namespace Soenneker.Flywheel.Core.Options;

/// <summary>Dashboard authentication and authorization configuration. Built-in credentials or a host authentication scheme are required.</summary>
public sealed class DashboardOptions
{
    internal bool UsesIndividualAccounts { get; set; }
    /// <summary>Individual accounts keyed by username. When populated, replaces the single Username/PasswordPhc account.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, DashboardAccount> Accounts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Change to revoke all built-in sessions, including live connections (checked at least once per second).</summary>
    public string SessionVersion { get; set; } = "1";

    /// <summary>Additional host-configured authentication schemes, such as an OIDC-backed cookie. Their users must have
    /// Flywheel.Reader or Flywheel.Operator roles and a nonempty name. The host owns their session revocation and MFA.</summary>
    public string[] AuthenticationSchemes { get; set; } = [];

    /// <summary>Prefix for HTTP and SignalR endpoints. Use / for no prefix. Must match the dashboard client's EnginePath; independent of its HomePath.</summary>
    public string EnginePath { get; set; } = "/flywheel";

    /// <summary>Username used to sign in to the dashboard.</summary>
    public string Username { get; set; } = "admin";

    /// <summary>PBKDF2 password hash encoded in PHC string format and used to verify dashboard credentials.</summary>
    public string PasswordPhc { get; set; } = "";

    /// <summary>Additional browser origins allowed to call the dashboard API and SignalR hub, including scheme and port (for example, https://localhost:7004). Empty allows same-origin browsers only. Wildcards, paths, queries, and fragments are not supported. This is not a replacement for authentication or network access controls.</summary>
    public string[] AllowedOrigins { get; set; } = [];
}
