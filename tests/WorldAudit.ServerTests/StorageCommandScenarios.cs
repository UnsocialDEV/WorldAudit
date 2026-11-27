using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using WorldAudit.Domain;
using WorldAudit.Infrastructure.Persistence;

namespace WorldAudit.ServerTests;

public sealed partial class ServerTests
{
    private async Task StorageCommandScenarios()
    {
        const string name = "mixed trunk rollback and undo through commands include the latest independent withdrawal";
        var trunk = Pos(556, 553);
        var chest = trunk.AddCopy(2, 0, 0);
        var ticks = 0;
        long listener = 0;
        byte[] expectedTool = [], expectedStone = [], expectedTrunkWithdrawal = [], expectedWithdrawal = [];
        try
        {
            await OnServer(() =>
            {
                Clear(trunk);
                Clear(chest);
                api.World.BlockAccessor.SetBlock(Block("game:trunk-east").Id, trunk);
                api.World.BlockAccessor.SetBlock(Block("game:chest-east").Id, chest);
                var trunkInventory = InventoryAt(trunk);
                var chestInventory = InventoryAt(chest);
                // Bob's deposits must survive Alice's selective rollback.
                var axe = Stack("game:axe-flint");
                axe.Attributes.SetInt("durability", 13);
                axe.Attributes.SetString("recovery-test", "mixed job");
                bob.Player.InventoryManager.OpenInventory(trunkInventory);
                Move(bob, trunkInventory, 0, axe);
                Move(bob, trunkInventory, 11, Stack("game:rock-granite", 64));
                Move(bob, trunkInventory, 3, Stack("game:rock-chert", 5));
                bob.Player.InventoryManager.CloseInventory(trunkInventory);
                bob.Player.InventoryManager.OpenInventory(chestInventory);
                Move(bob, chestInventory, 2, Stack("game:rock-basalt", 7));
                bob.Player.InventoryManager.CloseInventory(chestInventory);
                expectedTool = trunkInventory[0].Itemstack!.ToBytes();
                expectedStone = trunkInventory[11].Itemstack!.ToBytes();
                expectedTrunkWithdrawal = trunkInventory[3].Itemstack!.ToBytes();
                expectedWithdrawal = chestInventory[2].Itemstack!.ToBytes();
                listener = api.Event.RegisterGameTickListener(_ => Interlocked.Increment(ref ticks), 50);
            });

            var previous = (await runtime.Repository.GetRollbackJobsAsync(1)).FirstOrDefault()?.Id ?? 0;
            await using (var connection = await new SqliteConnectionFactory(runtime.Options).OpenConnectionAsync())
            await using (var held = (SqliteTransaction)await connection.BeginTransactionAsync())
            {
                // Delay the real writer, including its preview flush, until several game ticks have elapsed.
                using var lockTable = connection.CreateCommand();
                lockTable.Transaction = held;
                lockTable.CommandText = "UPDATE container_transactions SET flags = flags WHERE 0;";
                await lockTable.ExecuteNonQueryAsync();

                var started = 0;
                await OnServer(() =>
                {
                    TakeStack(InventoryAt(trunk), 3, 5);
                    Equip(alice, null);
                    PlaceBreak(alice, trunk, false);
                    Require(api.World.BlockAccessor.GetBlock(trunk).Id == 0, "Alice did not break the filled trunk.");
                    TakeStack(InventoryAt(chest), 2, 7);
                    Require(runtime.AuditWriter.Snapshot().PendingEvents > 0, "Writer delay did not retain the latest changes.");
                    started = Volatile.Read(ref ticks);
                    Command($"/wa rollback u:{alice.Player.PlayerName} r:5");
                });
                await Task.Delay(700);
                Require(Volatile.Read(ref ticks) - started >= 3, "Rollback preview blocked the game thread while the writer was delayed.");
                await held.RollbackAsync();
            }

            var rollback = await NewPreview(previous);
            Require(rollback.PlannedContainerCount == 2, $"Expected only the two independent withdrawals, found {rollback.PlannedContainerCount} container entries.");
            Require(rollback.PlannedBlockCount > 0, "Preview missed the trunk break.");
            await VerifyJobPage("blocks", "Broke", rollback.Id);
            var storageLines = await VerifyJobPage("storage", $"Details  /wa job {rollback.Id} storage details", rollback.Id);
            var detailHint = storageLines.First(line => line.Contains(" storage details "));
            var anchor = long.Parse(detailHint[(detailHint.LastIndexOf(' ') + 1)..]);
            await VerifyJobPage($"storage details {anchor}", "(slot ", rollback.Id);
            await OnServer(() =>
            {
                Command($"/wa apply {rollback.Id}");
                runtime.ProcessRollbackTickAsync().GetAwaiter().GetResult();
                runtime.RollbackCoordinator.SuspendAsync().GetAwaiter().GetResult();
            });
            Require((await runtime.Repository.GetRollbackJobAsync(rollback.Id))?.State == RollbackJobState.Paused,
                "Mixed job did not pause after its first batch.");
            await ApplyAndVerify(rollback);
            await OnServer(() =>
            {
                var inventory = InventoryAt(trunk);
                Require(expectedTool.AsSpan().SequenceEqual(inventory[0].Itemstack?.ToBytes()), "Tool quantity or attributes differ after mixed rollback.");
                Require(expectedTrunkWithdrawal.AsSpan().SequenceEqual(inventory[3].Itemstack?.ToBytes()), "An earlier independent withdrawal at the broken trunk was lost.");
                Require(expectedStone.AsSpan().SequenceEqual(inventory[11].Itemstack?.ToBytes()), "Trunk stone quantity differs after mixed rollback.");
                Require(expectedWithdrawal.AsSpan().SequenceEqual(InventoryAt(chest)[2].Itemstack?.ToBytes()), "Latest independent withdrawal was missing or replayed incorrectly.");
                Command($"/wa undo {rollback.Id}");
            });
            var undo = await NewPreview(rollback.Id);
            await ApplyAndVerify(undo);
            await OnServer(() =>
            {
                Require(api.World.BlockAccessor.GetBlock(trunk).Id == 0, "Undo did not return the broken trunk to air.");
                Require(InventoryAt(chest)[2].Empty, "Undo did not restore the independently emptied chest.");
            });
            results.Add(new { name, passed = true, rollback = rollback.Id, undo = undo.Id });
        }
        catch (Exception error)
        {
            results.Add(new { name, passed = false, error = error.ToString() });
        }
        finally
        {
            if (listener != 0) await OnServer(() => api.Event.UnregisterGameTickListener(listener));
        }

        async Task<List<string>> VerifyJobPage(string section, string expected, long job)
        {
            await OnServer(() =>
            {
                AuditChatPackets.Read(alice);
                Command($"/wa job {job} {section}");
            });
            var lines = new List<string>();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                await OnServer(() => lines.AddRange(AuditChatPackets.Read(alice)
                    .Select(packet => packet.Chatline?.Message).OfType<string>()));
                if (lines.Any(line => line.Contains(expected))) return lines;
                await Task.Delay(40);
            }
            throw new InvalidOperationException($"Missing {section} page for job {job}: {string.Join(" | ", lines)}");
        }

        async Task ApplyAndVerify(RollbackJob preview)
        {
            await OnServer(() => Command($"/wa apply {preview.Id}"));
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(8))
            {
                var job = await runtime.Repository.GetRollbackJobAsync(preview.Id);
                if (job?.State is RollbackJobState.Queued or RollbackJobState.Running or RollbackJobState.Previewed)
                {
                    await Task.Delay(40);
                    continue;
                }
                Require(job?.State == RollbackJobState.Completed, $"Job {preview.Id} ended in {job?.State}: {job?.LastError}.");
                var expected = preview.PlannedContainerCount + preview.PlannedBlockCount;
                var applied = await runtime.Repository.GetRollbackJobEntryDetailsAsync(preview.Id, RollbackJobEntryResult.Applied, expected + 1);
                Require(expected > 0 && applied.Count == expected, $"Only {applied.Count} of {expected} entries applied in job {preview.Id}.");
                return;
            }
            throw new TimeoutException($"Rollback job {preview.Id} did not complete on normal game ticks.");
        }
    }

    private InventoryBase InventoryAt(BlockPos pos) =>
        (InventoryBase)((IBlockEntityContainer)api.World.BlockAccessor.GetBlockEntity(pos)).Inventory;

    private void TakeStack(InventoryBase inventory, int slot, int quantity)
    {
        alice.Player.InventoryManager.OpenInventory(inventory);
        var cursor = alice.Player.InventoryManager.MouseItemSlot;
        cursor.Itemstack = null;
        var take = new ItemStackMoveOperation(api.World, EnumMouseButton.Left, 0,
            EnumMergePriority.DirectMerge, quantity) { ActingPlayer = alice.Player };
        var click = (Packet_Client)inventory.InvNetworkUtil.GetActivateSlotPacket(slot, take);
        ((InventoryNetworkUtil)inventory.InvNetworkUtil).HandleClientPacket(alice.Player, 7, click);
        Require(inventory[slot].Empty && cursor.StackSize == quantity, $"Could not withdraw {quantity} from slot {slot}.");
        alice.Player.InventoryManager.CloseInventory(inventory);
    }

    private Task OnServer(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Event.EnqueueMainThreadTask(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception error) { done.SetException(error); }
        }, "worldaudit-test-player-command");
        return done.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }

    private void Command(string command)
    {
        TextCommandResult? response = null;
        api.ChatCommands.ExecuteUnparsed(command, new TextCommandCallingArgs
        {
            Caller = new Caller { Player = alice.Player, Type = EnumCallerType.Player, CallerPrivileges = ["*"] },
            LanguageCode = "en"
        }, result => response = result);
        Require(response?.Status != EnumCommandStatus.Error, $"{command} failed: {response?.StatusMessage}");
    }

    private async Task<RollbackJob> NewPreview(long after)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            var latest = (await runtime.Repository.GetRollbackJobsAsync(1)).FirstOrDefault();
            if (latest is { State: RollbackJobState.Previewed } && latest.Id > after) return latest;
            await Task.Delay(40);
        }
        throw new TimeoutException($"Player command did not persist a preview after job {after}.");
    }
}
