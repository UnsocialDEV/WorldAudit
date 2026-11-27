using System.Diagnostics;
using Vintagestory.API.MathTools;
using WorldAudit.Domain;

namespace WorldAudit.ServerTests;

public sealed partial class ServerTests
{
    private async Task FilterCommandScenarios()
    {
        const string name = "lookup and rollback distinguish block breaks, storage labels and moved-item codes";
        var broken = Pos(565, 532);
        var chest = broken.AddCopy(-1, 0, 0);
        var trunk = broken.AddCopy(1, 0, 0);
        try
        {
            await OnServer(() =>
            {
                foreach (var pos in new[] { broken, chest, trunk }) Clear(pos);
                api.World.BlockAccessor.SetBlock(Block("game:rock-basalt").Id, broken);
                api.World.BlockAccessor.SetBlock(Block("game:chest-east").Id, chest);
                api.World.BlockAccessor.SetBlock(Block("game:trunk-east").Id, trunk);

                var chestInventory = InventoryAt(chest);
                var trunkInventory = InventoryAt(trunk);
                bob.Player.InventoryManager.OpenInventory(chestInventory);
                Move(bob, chestInventory, 0, Stack("game:rock-granite", 7));
                bob.Player.InventoryManager.CloseInventory(chestInventory);
                bob.Player.InventoryManager.OpenInventory(trunkInventory);
                Move(bob, trunkInventory, 1, Stack("game:rock-granite", 9));
                Move(bob, trunkInventory, 2, Stack("game:rock-chert", 3));
                bob.Player.InventoryManager.CloseInventory(trunkInventory);

                TakeStack(chestInventory, 0, 7);
                TakeStack(trunkInventory, 1, 9);
                TakeStack(trunkInventory, 2, 3);
                Equip(alice, null);
                PlaceBreak(alice, broken, false);
                Require(api.World.BlockAccessor.GetBlock(broken).Id == 0, "Fixture basalt block was not broken.");
            });
            await runtime.FlushAsync();

            var worldId = runtime.Options.WorldId!;
            var blockHistory = await runtime.Repository.GetBlockHistoryAsync(new(worldId, AuditPos(broken)));
            var breakId = blockHistory.Single(row => row.Action == BlockAuditAction.Break && row.Actor.ExternalId == alice.Player.PlayerUID).Id!.Value;
            var chestHistory = await runtime.Repository.GetContainerHistoryAsync(new(worldId, AuditPos(chest)));
            var trunkHistory = await runtime.Repository.GetContainerHistoryAsync(new(worldId, AuditPos(trunk)));
            var chestGranite = Withdrawal(chestHistory, "game:rock-granite", -7);
            var trunkGranite = Withdrawal(trunkHistory, "game:rock-granite", -9);
            var trunkChert = Withdrawal(trunkHistory, "game:rock-chert", -3);
            await Verify("a:break", RollbackJobEntryTargetType.Block, (broken, "action: break", breakId));
            await Verify("i:game:chest -c", RollbackJobEntryTargetType.Container, (chest, "-7 Rock Granite slot 0", chestGranite));
            await Verify("i:game:rock-granite -c", RollbackJobEntryTargetType.Container,
                (chest, "-7 Rock Granite slot 0", chestGranite), (trunk, "-9 Rock Granite slot 1", trunkGranite));
            await Verify("i:game:rock-chert -c", RollbackJobEntryTargetType.Container, (trunk, "-3 Rock Chert slot 2", trunkChert));

            results.Add(new { name, passed = true });
        }
        catch (Exception error)
        {
            results.Add(new { name, passed = false, error = error.ToString() });
        }

        long Withdrawal(IReadOnlyList<ContainerAuditTransaction> history, string code, int quantity) =>
            history.Single(row => row.Actor.ExternalId == alice.Player.PlayerUID
                && row.Lines.Any(line => line.ItemCode == code && line.QuantityDelta == quantity)).Id!.Value;

        async Task Verify(string query, RollbackJobEntryTargetType type, params (BlockPos Position, string Detail, long SourceId)[] expected)
        {
            var filters = $"u:{alice.Player.PlayerName} r:4 {query}";
            await OnServer(() =>
            {
                AuditChatPackets.Read(alice);
                Command("/wa lookup " + filters);
            });
            var lines = new List<string>();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                await OnServer(() => lines.AddRange(AuditChatPackets.Read(alice)
                    .Select(packet => packet.Chatline?.Message).OfType<string>()));
                if (lines.Any(line => line.Contains("Lookup at", StringComparison.Ordinal))) break;
                await Task.Delay(40);
            }
            var rows = lines.Where(line => line.Contains(" (dimension ", StringComparison.Ordinal)).ToArray();
            Require(rows.Length == expected.Length, $"Lookup {query} returned unexpected records: {string.Join(" | ", lines)}");
            foreach (var (pos, detail, _) in expected)
            {
                var display = pos.Copy().ToLocalPosition(api);
                var index = lines.FindIndex(line => line.Contains($" at {display.X}, {display.Y}, {display.Z} (dimension {pos.dimension})", StringComparison.Ordinal));
                Require(index >= 0 && lines[index].Contains(detail, StringComparison.Ordinal),
                    $"Lookup {query} missed the expected change at {pos}: {string.Join(" | ", lines)}");
            }
            var previous = (await runtime.Repository.GetRollbackJobsAsync(1)).FirstOrDefault()?.Id ?? 0;
            await OnServer(() => Command("/wa rollback " + filters));
            var preview = await NewPreview(previous);
            var entries = await runtime.Repository.GetRollbackJobEntryDetailsAsync(preview.Id, RollbackJobEntryResult.Planned, 12);
            Require(entries.Count == expected.Length && entries.All(entry => entry.TargetType == type)
                && expected.All(row => entries.Any(entry => entry.Position == AuditPos(row.Position) && entry.SourceRecordId == row.SourceId)),
                $"Rollback {query} selected the wrong records: {string.Join(", ", entries.Select(entry => $"{entry.TargetType} {entry.Position}"))}");
            await OnServer(() => Command($"/wa cancel {preview.Id}"));
        }
    }
}
