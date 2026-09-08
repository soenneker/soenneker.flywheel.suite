using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Soenneker.Flywheel.Core.Dashboard;

internal sealed class DashboardOriginPolicy
{
    private readonly HashSet<string> _origins;

    public DashboardOriginPolicy(IEnumerable<string> origins)
    {
        _origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string origin in origins)
        {
            if (!TryNormalize(origin, out string normalized))
                throw new InvalidOperationException("Flywheel AllowedOrigins entries must be HTTP(S) origins without wildcards, credentials, paths, queries, or fragments.");
            _origins.Add(normalized);
        }
    }

    public bool IsAllowed(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out StringValues origins))
            return true; // Non-browser clients still require authentication and antiforgery validation.
        if (origins.Count != 1 || !TryNormalize(origins[0], out string origin))
            return false;
        return _origins.Contains(origin) ||
               (TryNormalize($"{request.Scheme}://{request.Host}", out string requestOrigin) &&
                string.Equals(origin, requestOrigin, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsAllowedCrossOrigin(string origin) => TryNormalize(origin, out string normalized) && _origins.Contains(normalized);

    private static bool TryNormalize(string? value, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("https" or "http") || uri.Host.Contains('*') ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
