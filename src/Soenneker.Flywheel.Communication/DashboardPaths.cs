namespace Soenneker.Flywheel.Communication;

/// <summary>Validates and combines dashboard and engine route prefixes.</summary>
public static class DashboardPaths
{
    /// <summary>Normalizes an absolute route prefix. Use / for no prefix; nested prefixes are supported.</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("A Flywheel path must start with a single slash.", nameof(path));
        string normalized = path.TrimEnd('/');
        if (normalized.Length == 0) return "/";
        foreach (string segment in normalized[1..].Split('/'))
            if (segment.Length == 0 || segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
                throw new ArgumentException("Flywheel path segments must contain only ASCII letters, digits, hyphens, or underscores.", nameof(path));
        return normalized;
    }

    /// <summary>Returns a relative endpoint path that preserves the configured backend address's application base.</summary>
    public static string Relative(string prefix, string endpoint) =>
        string.Join("/", new[] { prefix.Trim('/'), endpoint.Trim('/') }.Where(segment => segment.Length > 0));
}
