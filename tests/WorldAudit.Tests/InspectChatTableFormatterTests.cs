using WorldAudit.Presentation.Chat;
using Vintagestory.API.Common;
using WorldAudit.Tests.TestSupport;

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

        var plain = lines.Select(PlainText).ToArray();
        Assert.Contains("[WA] Inspect: 120, 65, -44", plain);
        Assert.Contains("Events 1–2 of 4     •     Page 1 of 2", plain);
        Assert.Contains("1. Block     2m ago", plain);
        Assert.Contains("Player  Dayton        Cause  Player", plain);
        Assert.Contains("Action  Broke Oak Planks", plain);
        Assert.Contains("Interact again for page 2 of 2.", plain);
        Assert.Equal("2026-03-17 11:58:00 UTC", plain[^1]);
        Assert.Equal(new[] { "2. Crate     5m ago", "1. Block     2m ago" },
            plain.Where(line => line.EndsWith(" ago", StringComparison.Ordinal)));
        Assert.True(lines.Count < 30);
    }

    [Fact]
    public void Format_UsesAvailableWidthForCompleteActionText()
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

        var plain = lines.Select(PlainText).ToArray();
        Assert.Contains("1. Crate     2m ago", plain);
        Assert.Contains("Player  Dayton        Cause  Player", plain);
        Assert.Contains("Action  Crate changed: +8 Oak Planks; -2 Firewood; +4 Stones; +6 Copper Bits", plain);

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
            "No audit history found for this location."
        ], lines.Select(PlainText));
    }
    [Fact]
    public void Format_EscapesUntrustedNamesAndKeepsBalancedVtml()
    {
        var lines = new InspectChatTableFormatter().Format("1, 2, 3", 1, 1, 1, 1, 1,
            [new InspectChatRow(DateTimeOffset.UtcNow, 1, "Block", "just now", "<font color=red>Player", "Player", "Placed <strong>item")], 1);
        var plain = lines.Select(PlainText).ToArray();
        Assert.Contains("Player  <font color=red>Player        Cause  Player", plain);
        Assert.Contains("Action  Placed <strong>item", plain);
        Assert.StartsWith("<font color=\"#F07070\"><strong>", lines[0]);
    }

    private static string PlainText(string markup)
    {
        var errors = new List<string>();
        var logger = ProxyFactory.Create<ILogger>(proxy => proxy.SetHandler("Error", args =>
        {
            errors.Add((string)args![0]!);
            return null;
        }));
        var tokens = VtmlParser.Tokenize(logger, markup);
        Assert.Empty(errors);
        return string.Concat(tokens.Select(Read));

        static string Read(VtmlToken token) => token is VtmlTextToken text
            ? text.Text
            : string.Concat(((VtmlTagToken)token).ChildElements.Select(Read));
    }

}
