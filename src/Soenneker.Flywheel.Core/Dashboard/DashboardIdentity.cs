using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Soenneker.Flywheel.Core.Options;

namespace Soenneker.Flywheel.Core.Dashboard;

internal static class DashboardIdentity
{
    internal const string StampClaim = "flywheel:credential-version";

    internal static DashboardAccount? Account(DashboardOptions options, string? username) =>
        username is null ? null : options.UsesIndividualAccounts || options.Accounts.Count != 0
            ? options.Accounts.GetValueOrDefault(username)
            : !string.IsNullOrWhiteSpace(options.PasswordPhc) && string.Equals(username, options.Username, StringComparison.Ordinal)
                ? new DashboardAccount { PasswordPhc = options.PasswordPhc, CanManage = true } : null;

    internal static string Stamp(DashboardOptions options, string username, DashboardAccount account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{username.Length}:{username}{account.PasswordPhc.Length}:{account.PasswordPhc}:{account.CanManage}:{options.SessionVersion}")));

    internal static bool Valid(DashboardOptions options, ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return false;
        if (principal.Identity.AuthenticationType != "Flywheel") return true;
        string? name = principal.Identity.Name;
        DashboardAccount? account = Account(options, name);
        return account is not null && principal.FindFirst(StampClaim)?.Value == Stamp(options, name!, account);
    }
}
