using WorldAudit.Domain;
using WorldAudit.Presentation.Chat;

namespace WorldAudit.Tests;

public sealed class ChatAuditFormatterTests
{
    [Fact]
    public void FormatBlockResults_ShowsSyntheticActorNames()
    {
        var formatter = new ChatAuditFormatter();
        var now = new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero);
        var results = formatter.FormatBlockResults(
        [
            new BlockAuditEvent(
                Id: 1,
                WorldId: "main",
                Position: new BlockPosition(2, 65, 3),
                OccurredAt: now.AddMinutes(-4),
                Actor: new AuditActor("#fire", null, true),
                Cause: AuditCause.Fire,
                Action: BlockAuditAction.Break,
                OldBlockCode: "game:drygrass",
                NewBlockCode: "game:air")
        ], now);

        Assert.Contains(results, line => line.Contains("#fire", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results, line => line.Contains("cause: fire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FormatContainerResults_DescribesQuantityDeltas()
    {
        var formatter = new ChatAuditFormatter();
        var now = new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero);
        var results = formatter.FormatContainerResults(
        [
            new ContainerAuditTransaction(
                Id: 1,
                WorldId: "main",
                Position: new BlockPosition(1, 65, 2),
                OccurredAt: now.AddMinutes(-10),
                Actor: new AuditActor("Dayton", "player-1", false),
                Cause: AuditCause.Player,
                InventoryType: "crate",
                ContainerLabel: "game:crate",
                BeforeSnapshot: null,
                AfterSnapshot: null,
                Lines:
                [
                    new ContainerAuditLine(1, "game:oakplanks", 0, 8, 0, 8)
                ])
        ], now);

        Assert.Contains(results, line => line.Contains("+8 Oakplanks", StringComparison.Ordinal));
        Assert.Contains(results, line => line.Contains("crate", StringComparison.OrdinalIgnoreCase));
    }
}
