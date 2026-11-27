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
        static string Text(string value) => Commands.WorldAuditCommandText.EscapeChatText(value);
        static string Heading(string value) => $"<font color=\"{AuditChatPalette.Accent}\"><strong>{Text(value)}</strong></font>";
        static string Label(string value) => $"<font color=\"{AuditChatPalette.Accent}\">{Text(value)}</font>";
        static string Value(string value) => $"<font color=\"{AuditChatPalette.Value}\">{Text(value)}</font>";

        var lines = new List<string>
        {
            Heading($"[WA] Inspect: {displayPosition}")
        };

        if (totalEventCount == 0)
        {
            lines.Add(Value("No audit history found for this location."));
            return lines;
        }

        lines.Add(Label($"Events {start}–{end} of {totalEventCount}     •     Page {pageNumber} of {totalPages}"));
        if (totalPages > 1)
        {
            lines.Add(Label($"Interact again for page {nextPage} of {totalPages}."));
        }

        foreach (var row in rows.Reverse())
        {
            lines.Add("");
            lines.Add(Heading($"{row.EventNumber}. {row.Type}") + "     " + Value(row.When));
            lines.Add(Label("Player  ") + Value(row.Who) + "        " + Label("Cause  ") + Value(row.Why));
            lines.Add(Label("Action  ") + Value(row.What));
            lines.Add($"<font color=\"{AuditChatPalette.Secondary}\">{Text(row.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))} UTC</font>");
            if (row.DetailId is long id) lines.Add(Label($"Details  /wa i details {id}"));
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
    string What,
    long? DetailId = null);
