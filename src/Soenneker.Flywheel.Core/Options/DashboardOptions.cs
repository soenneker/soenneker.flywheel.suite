namespace Soenneker.Flywheel.Core.Options;

/// <summary>Dashboard credentials are required in every environment.</summary>
public sealed class DashboardOptions
{
    /// <summary>Prefix for HTTP and SignalR endpoints. Use / for no prefix. Must match the dashboard client's EnginePath; independent of its HomePath.</summary>
    public string EnginePath { get; set; } = "/flywheel";

    /// <summary>Username used to sign in to the dashboard.</summary>
    public string Username { get; set; } = "admin";
    /// <summary>PBKDF2 password hash encoded in PHC string format and used to verify dashboard credentials.</summary>
    public string PasswordPhc { get; set; } = "";

    /// <summary>Additional browser origins allowed to call the dashboard API and SignalR hub, including scheme and port (for example, https://localhost:7004). Empty allows same-origin browsers only. Wildcards, paths, queries, and fragments are not supported. This is not a replacement for authentication or network access controls.</summary>
    public string[] AllowedOrigins { get; set; } = [];
}
