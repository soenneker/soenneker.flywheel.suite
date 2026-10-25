namespace Soenneker.Flywheel.Core.Options;

/// <summary>An individual dashboard account. Removing an account or changing its credentials revokes its sessions.</summary>
public sealed record DashboardAccount
{
    /// <summary>PBKDF2 password hash in PHC format.</summary>
    public required string PasswordPhc { get; init; }

    /// <summary>Allows administrative actions. False grants read-only access.</summary>
    public bool CanManage { get; init; }
}
