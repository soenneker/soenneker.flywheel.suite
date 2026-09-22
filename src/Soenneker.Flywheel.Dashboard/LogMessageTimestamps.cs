using System.Globalization;
using System.Text.RegularExpressions;

namespace Soenneker.Flywheel.Dashboard;

internal static partial class LogMessageTimestamps
{
    // Require a complete date, time, and explicit offset. Never guess the meaning of
    // unqualified dates, epoch-looking numbers, or identifiers inside message text.
    [GeneratedRegex(@"(?<![\w.+-])(?<date>\d{4}-\d{2}-\d{2})(?<separator>[Tt ])(?<time>\d{2}:\d{2}:\d{2})(?<fraction>\.\d{1,7})?[ ]*(?<zone>Z|UTC(?:[+-]\d{2}:?\d{2})?|[+-]\d{2}:?\d{2})(?![\w:+-]|\.\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex TimestampPattern();

    public static string Convert(string message, TimeZoneInfo zone)
    {
        try
        {
            return TimestampPattern().Replace(message, match =>
            {
                string sourceZone = match.Groups["zone"].Value;
                string offset = sourceZone.ToUpperInvariant().Replace("UTC", "").Replace("Z", "+00:00");
                if (offset.Length == 0) offset = "+00:00";
                if (offset.Length == 5) offset = offset.Insert(3, ":");
                string fraction = match.Groups["fraction"].Value;
                string input = $"{match.Groups["date"].Value}T{match.Groups["time"].Value}{fraction}{offset}";
                if (!DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset instant))
                    return match.Value;

                string format = "yyyy-MM-dd'" + match.Groups["separator"].Value + "'HH:mm:ss";
                if (fraction.Length > 0) format += "." + new string('f', fraction.Length - 1);
                format += sourceZone.StartsWith("UTC", StringComparison.OrdinalIgnoreCase) ? " 'UTC'zzz" : "zzz";
                return TimeZoneInfo.ConvertTime(instant, zone).ToString(format, CultureInfo.InvariantCulture);
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return message;
        }
    }
}
