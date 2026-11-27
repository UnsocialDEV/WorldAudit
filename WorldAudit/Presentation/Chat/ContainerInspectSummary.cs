using WorldAudit.Domain;
using WorldAudit.Presentation.Commands;

namespace WorldAudit.Presentation.Chat;

internal static class ContainerInspectSummary
{
    internal static InspectChatRow Format(ContainerInspectEntry entry, DateTimeOffset now)
    {
        var label = WorldAuditCommandText.FriendlyName(entry.Label);
        var type = entry.VisitId switch
        {
            null => label,
            var id when id.StartsWith("legacy:", StringComparison.Ordinal) => $"{label} · grouped activity",
            _ => $"{label} · visit"
        };
        var movements = new List<string>();
        foreach (var group in entry.Items.GroupBy(item => Math.Sign(item.Delta)).OrderBy(g => g.Key))
        {
            var verb = group.Key switch { -1 => "Removed", 1 => "Added", _ => "Modified" };
            const int previewSize = 5;
            var items = group.OrderByDescending(item => Math.Abs(item.Delta)).Take(previewSize)
                .Select(item => item.Delta == 0 ? WorldAuditCommandText.FriendlyName(item.Code)
                    : $"{Math.Abs(item.Delta)} {WorldAuditCommandText.FriendlyName(item.Code)}");
            var remaining = group.Count() - previewSize;
            var more = remaining > 0 ? $" (+{remaining} more item types)" : string.Empty;
            movements.Add($"{verb} {string.Join(", ", items)}{more}");
        }
        var when = WorldAuditCommandText.FormatRelativeTime(entry.LastChange, now);
        if (entry.Transactions > 1)
        {
            var duration = entry.LastChange - entry.FirstChange;
            when += $" · {entry.Transactions} transactions over {duration.TotalSeconds:0.#}s";
        }
        return new(entry.LastChange, 0, type, when, entry.Actor, entry.Cause.ToString(),
            movements.Count == 0 ? "No item delta" : string.Join("\n", movements), entry.DetailId);
    }
}
