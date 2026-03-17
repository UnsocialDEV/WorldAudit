using System.Globalization;
using WorldAudit.Application.Abstractions;
using WorldAudit.Domain;

namespace WorldAudit.Presentation.Chat;

public sealed class ChatAuditFormatter : IResultFormatter
{
    public IReadOnlyList<string> FormatBlockResults(IReadOnlyList<BlockAuditEvent> events, DateTimeOffset now)
    {
        if (events.Count == 0)
        {
            return ["[WA] No matching block history found."];
        }

        var lines = new List<string>(events.Count * 2);
        foreach (var auditEvent in events)
        {
            lines.Add($"[WA] {FormatRelativeTime(auditEvent.OccurredAt, now)} {auditEvent.Actor.Name} {DescribeAction(auditEvent)} at {auditEvent.Position.X}, {auditEvent.Position.Y}, {auditEvent.Position.Z}");
            lines.Add($"[WA]      cause: {auditEvent.Cause.ToString().ToLowerInvariant()} | world: {auditEvent.WorldId} | action: {auditEvent.Action.ToString().ToLowerInvariant()}");
        }

        return lines;
    }

    public IReadOnlyList<string> FormatContainerResults(IReadOnlyList<ContainerAuditTransaction> transactions, DateTimeOffset now)
    {
        if (transactions.Count == 0)
        {
            return ["[WA] No matching container history found."];
        }

        var lines = new List<string>(transactions.Count * 2);
        foreach (var transaction in transactions)
        {
            lines.Add($"[WA] {FormatRelativeTime(transaction.OccurredAt, now)} {transaction.Actor.Name} changed {FriendlyName(transaction.ContainerLabel ?? transaction.InventoryType)} at {transaction.Position.X}, {transaction.Position.Y}, {transaction.Position.Z}");
            lines.Add($"[WA]      cause: {transaction.Cause.ToString().ToLowerInvariant()} | inventory: {transaction.InventoryType} | lines: {DescribeContainerLines(transaction.Lines)}");
        }

        return lines;
    }

    private static string DescribeAction(BlockAuditEvent auditEvent)
    {
        return auditEvent.Action switch
        {
            BlockAuditAction.Place => $"placed {FriendlyName(auditEvent.NewBlockCode)}",
            BlockAuditAction.Break => $"broke {FriendlyName(auditEvent.OldBlockCode)} -> Air",
            BlockAuditAction.Restore => $"restored {FriendlyName(auditEvent.NewBlockCode)}",
            _ => $"replaced {FriendlyName(auditEvent.OldBlockCode)} -> {FriendlyName(auditEvent.NewBlockCode)}"
        };
    }

    private static string FriendlyName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.EndsWith(":air", StringComparison.OrdinalIgnoreCase))
        {
            return "Air";
        }

        var value = code.Split(':', 2).Last().Replace('-', ' ').Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value);
    }

    private static string DescribeContainerLines(IReadOnlyList<ContainerAuditLine> lines)
    {
        if (lines.Count == 0)
        {
            return "no item delta";
        }

        return string.Join("; ", lines.Select(line =>
        {
            var direction = line.QuantityDelta > 0 ? "+" : string.Empty;
            var slotText = line.SlotId.HasValue ? $" slot {line.SlotId.Value}" : string.Empty;
            return $"{direction}{line.QuantityDelta} {FriendlyName(line.ItemCode)}{slotText}";
        }));
    }

    private static string FormatRelativeTime(DateTimeOffset occurredAt, DateTimeOffset now)
    {
        var delta = now - occurredAt;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)delta.TotalMinutes)}m ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)delta.TotalHours)}h ago";
        }

        return $"{Math.Max(1, (int)delta.TotalDays)}d ago";
    }
}
