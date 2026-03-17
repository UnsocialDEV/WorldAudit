namespace WorldAudit.Presentation.Chat;

internal sealed class InspectChatTableFormatter
{
    public IReadOnlyList<string> Format(
        string displayPosition,
        int pageNumber,
        int totalPages,
        int start,
        int end,
        int totalEventCount,
        IReadOnlyList<InspectChatRow> rows,
        int nextPage)
    {
        const int detailWidth = 44;

        static IReadOnlyList<string> Wrap(string value, int width)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return [string.Empty];
            }

            var parts = new List<string>();
            var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = string.Empty;

            foreach (var word in words)
            {
                if (word.Length > width)
                {
                    if (!string.IsNullOrEmpty(current))
                    {
                        parts.Add(current);
                        current = string.Empty;
                    }

                    for (var index = 0; index < word.Length; index += width)
                    {
                        parts.Add(word.Substring(index, Math.Min(width, word.Length - index)));
                    }

                    continue;
                }

                var candidate = string.IsNullOrEmpty(current)
                    ? word
                    : $"{current} {word}";

                if (candidate.Length > width)
                {
                    parts.Add(current);
                    current = word;
                    continue;
                }

                current = candidate;
            }

            if (!string.IsNullOrEmpty(current))
            {
                parts.Add(current);
            }

            return parts;
        }

        var lines = new List<string>(rows.Count * 3 + 5)
        {
            $"[WA] Inspect: {displayPosition}"
        };

        if (totalEventCount == 0)
        {
            lines.Add("[WA] No audit history found for this location.");
            return lines;
        }

        lines.Add($"[WA] Events {start}-{end} of {totalEventCount} | page {pageNumber}/{totalPages}");

        foreach (var row in rows)
        {
            lines.Add($"[WA] {row.EventNumber}. {row.Type} | {row.When}");
            lines.Add($"[WA]    by: {row.Who} | cause: {row.Why}");

            var wrappedWhat = Wrap(row.What, detailWidth);
            for (var index = 0; index < wrappedWhat.Count; index++)
            {
                var prefix = index == 0 ? "[WA]    " : "[WA]      ";
                lines.Add($"{prefix}{wrappedWhat[index]}");
            }
        }

        if (totalPages > 1)
        {
            lines.Add($"[WA] Interact with this location again for page {nextPage}/{totalPages}.");
        }

        return lines;
    }
}

internal sealed record InspectChatRow(
    DateTimeOffset OccurredAt,
    int EventNumber,
    string Type,
    string When,
    string Who,
    string Why,
    string What);
