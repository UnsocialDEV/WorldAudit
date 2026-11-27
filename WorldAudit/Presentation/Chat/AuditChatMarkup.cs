namespace WorldAudit.Presentation.Chat;

internal static class AuditChatMarkup
{
    public static string Render(string message, bool error = false)
    {
        static string Text(string value) => Commands.WorldAuditCommandText.EscapeChatText(value);
        static string Accent(string value) => $"<font color=\"{AuditChatPalette.Accent}\"><strong>{Text(value)}</strong></font>";

        string Line(string line)
        {
            if (error) return Accent("Error  ") + Text(line);
            var help = line.IndexOf(" — ", StringComparison.Ordinal);
            if (help >= 0) return Accent(line[..help]) + Text(line[help..]);
            var label = line.IndexOf(": ", StringComparison.Ordinal);
            if (label >= 0) return Accent(line[..(label + 2)]) + Text(line[(label + 2)..]);
            return Accent(line);
        }

        return $"<font color=\"{AuditChatPalette.Value}\">{string.Join("\n", message.Split('\n').Select(Line))}</font>";
    }
}
