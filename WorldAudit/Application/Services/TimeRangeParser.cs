using System.Globalization;
using System.Text.RegularExpressions;
using WorldAudit.Domain;

namespace WorldAudit.Application.Services;

public static partial class TimeRangeParser
{
    [GeneratedRegex("(\\d+)([smhdw])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationSegmentRegex();

    public static AuditTimeRange Parse(string input, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var parts = input.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1)
        {
            var duration = ParseDurationToken(parts[0]);
            return new AuditTimeRange(now - duration, now);
        }

        if (parts.Length == 2)
        {
            var first = ParseDurationToken(parts[0]);
            var second = ParseDurationToken(parts[1]);
            var older = first >= second ? first : second;
            var newer = first < second ? first : second;
            return new AuditTimeRange(now - older, now - newer);
        }

        throw new FormatException($"Invalid time range '{input}'.");
    }

    public static TimeSpan ParseDurationToken(string input)
    {
        var matches = DurationSegmentRegex().Matches(input);

        if (matches.Count == 0)
        {
            throw new FormatException($"Invalid duration '{input}'.");
        }

        var consumed = string.Concat(matches.Select(match => match.Value));
        if (!string.Equals(consumed, input, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Invalid duration '{input}'.");
        }

        var total = TimeSpan.Zero;
        foreach (Match match in matches)
        {
            var value = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = char.ToLowerInvariant(match.Groups[2].Value[0]);

            total += unit switch
            {
                's' => TimeSpan.FromSeconds(value),
                'm' => TimeSpan.FromMinutes(value),
                'h' => TimeSpan.FromHours(value),
                'd' => TimeSpan.FromDays(value),
                'w' => TimeSpan.FromDays(value * 7d),
                _ => throw new FormatException($"Invalid duration unit '{unit}'.")
            };
        }

        return total;
    }
}
