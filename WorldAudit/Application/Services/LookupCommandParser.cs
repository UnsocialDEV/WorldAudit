using WorldAudit.Domain;

namespace WorldAudit.Application.Services;

public sealed class LookupCommandParser
{
    public LookupFilters Parse(IEnumerable<string> tokens, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        AuditTimeRange? timeRange = null;
        int? radius = null;
        string? actorName = null;
        AuditCause? cause = null;
        BlockAuditAction? action = null;
        int? pageSize = null;
        List<string>? includeCodes = null;
        List<string>? excludeCodes = null;
        var includeBlocks = true;
        var includeContainers = true;
        var explicitScope = false;

        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (string.Equals(token, "-b", StringComparison.OrdinalIgnoreCase))
            {
                if (!explicitScope)
                {
                    includeBlocks = false;
                    includeContainers = false;
                    explicitScope = true;
                }

                includeBlocks = true;
                explicitScope = true;
                continue;
            }

            if (string.Equals(token, "-c", StringComparison.OrdinalIgnoreCase))
            {
                if (!explicitScope)
                {
                    includeBlocks = false;
                    includeContainers = false;
                    explicitScope = true;
                }

                includeContainers = true;
                explicitScope = true;
                continue;
            }

            if (token.StartsWith("t:", StringComparison.OrdinalIgnoreCase))
            {
                timeRange = TimeRangeParser.Parse(token[2..], now);
                continue;
            }

            if (token.StartsWith("r:", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(token[2..], out var parsedRadius) || parsedRadius <= 0)
                {
                    throw new FormatException($"Invalid radius token '{token}'.");
                }

                radius = parsedRadius;
                continue;
            }

            if (token.StartsWith("u:", StringComparison.OrdinalIgnoreCase))
            {
                var value = token[2..];
                if (TryParseCause(value, out var parsedCause))
                {
                    cause = parsedCause;
                }
                else
                {
                    actorName = value;
                }

                continue;
            }

            if (token.StartsWith("a:", StringComparison.OrdinalIgnoreCase))
            {
                action = ParseAction(token[2..]);
                continue;
            }

            if (token.StartsWith("i:", StringComparison.OrdinalIgnoreCase))
            {
                includeCodes = ParseCodes(token, token[2..]);
                continue;
            }

            if (token.StartsWith("e:", StringComparison.OrdinalIgnoreCase))
            {
                excludeCodes = ParseCodes(token, token[2..]);
                continue;
            }

            if (token.StartsWith("p:", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(token[2..], out var parsedPageSize) || parsedPageSize <= 0)
                {
                    throw new FormatException($"Invalid page size token '{token}'.");
                }

                pageSize = parsedPageSize;
            }
        }

        if (!explicitScope)
        {
            includeBlocks = true;
            includeContainers = true;
        }

        return new LookupFilters(
            timeRange,
            radius,
            actorName,
            cause,
            action,
            pageSize,
            includeCodes,
            excludeCodes,
            includeBlocks,
            includeContainers);
    }

    private static bool TryParseCause(string value, out AuditCause cause)
    {
        var normalized = value.Trim().TrimStart('#');
        return Enum.TryParse(normalized, true, out cause);
    }

    private static BlockAuditAction ParseAction(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "place" or "placed" => BlockAuditAction.Place,
            "break" or "broke" => BlockAuditAction.Break,
            "replace" or "replaced" => BlockAuditAction.Replace,
            "restore" or "restored" => BlockAuditAction.Restore,
            _ => throw new FormatException($"Unknown action '{value}'.")
        };
    }

    private static List<string> ParseCodes(string token, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            throw new FormatException($"Invalid code filter token '{token}'.");
        }

        var parsed = new List<string>();
        foreach (var segment in rawValue.Split(','))
        {
            var code = segment.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new FormatException($"Invalid code filter token '{token}'.");
            }

            if (!parsed.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                parsed.Add(code);
            }
        }

        return parsed;
    }
}
