using WorldAudit.Presentation.Chat;

namespace WorldAudit.Tests;

public sealed class InspectChatTableFormatterTests
{
    [Fact]
    public void Format_RendersTableHeadersAndRows()
    {
        var formatter = new InspectChatTableFormatter();
        var now = new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero);

        var lines = formatter.Format(
        "120, 65, -44",
        pageNumber: 1,
        totalPages: 2,
        start: 1,
        end: 2,
        totalEventCount: 4,
        rows:
        [
            new InspectChatRow(now.AddMinutes(-2), 1, "Block", "2m ago", "Dayton", "Player", "Broke Oak Planks"),
            new InspectChatRow(now.AddMinutes(-5), 2, "Crate", "5m ago", "#fire", "Fire", "Crate changed: +8 Oak Planks")
        ],
        nextPage: 2);

        Assert.Contains("[WA] Inspect: 120, 65, -44", lines);
        Assert.Contains("[WA] Events 1-2 of 4 | page 1/2", lines);
        Assert.Contains("[WA] 1. Block | 2m ago", lines);
        Assert.Contains("[WA]    by: Dayton | cause: Player", lines);
        Assert.Contains("[WA]    Broke Oak Planks", lines);
        Assert.Contains("[WA] Interact with this location again for page 2/2.", lines);
    }

    [Fact]
    public void Format_WrapsLongActionTextAcrossDetailRows()
    {
        var formatter = new InspectChatTableFormatter();
        var now = new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero);

        var lines = formatter.Format(
            "120, 65, -44",
            pageNumber: 1,
            totalPages: 1,
            start: 1,
            end: 1,
            totalEventCount: 1,
            rows:
            [
                new InspectChatRow(
                    now.AddMinutes(-2),
                    1,
                    "Crate",
                    "2m ago",
                    "Dayton",
                    "Player",
                    "Crate changed: +8 Oak Planks; -2 Firewood; +4 Stones; +6 Copper Bits")
            ],
            nextPage: 1);

        Assert.Contains("[WA] 1. Crate | 2m ago", lines);
        Assert.Contains("[WA]    by: Dayton | cause: Player", lines);
        Assert.True(lines.Count(line => line.StartsWith("[WA]    Crate changed:", StringComparison.Ordinal) || line.StartsWith("[WA]      ", StringComparison.Ordinal)) >= 2);
    }

    [Fact]
    public void Format_ShowsNoHistoryMessage_WhenNoRowsExist()
    {
        var formatter = new InspectChatTableFormatter();

        var lines = formatter.Format(
            "120, 65, -44",
            pageNumber: 1,
            totalPages: 1,
            start: 0,
            end: 0,
            totalEventCount: 0,
            rows: [],
            nextPage: 1);

        Assert.Equal(
        [
            "[WA] Inspect: 120, 65, -44",
            "[WA] No audit history found for this location."
        ], lines);
    }
}
