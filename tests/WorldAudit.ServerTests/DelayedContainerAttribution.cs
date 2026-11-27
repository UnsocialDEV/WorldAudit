using Vintagestory.API.Common;
using WorldAudit.Application.Services;
using WorldAudit.Domain;

namespace WorldAudit.ServerTests;

public sealed partial class ServerTests
{
    private async Task DelayedContainerAttribution()
    {
        const string name = "delayed and independent shared-container changes retain the correct actor";
        var pos = Pos(554, 540);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = 3;
        try
        {
            api.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    Clear(pos);
                    api.World.BlockAccessor.SetBlock(Block("game:chest-east").Id, pos);
                    var inventory = (InventoryBase)((IBlockEntityContainer)api.World.BlockAccessor.GetBlockEntity(pos)).Inventory;
                    alice.Player.InventoryManager.OpenInventory(inventory);
                    bob.Player.InventoryManager.OpenInventory(inventory);
                    inventory.SlotModified += Schedule;
                    try
                    {
                        Move(alice, inventory, 0, Stack("game:rock-granite"));
                        Move(bob, inventory, 1, Stack("game:rock-basalt"));
                    }
                    finally { inventory.SlotModified -= Schedule; }

                    // The same inventory and viewers alone do not identify this separate write.
                    api.Event.RegisterCallback(_ => Change(4), 20);

                    void Schedule(int slot)
                    {
                        if (slot == 0) api.Event.RegisterCallback(_ => Change(2), 80);
                        if (slot == 1) api.Event.EnqueueMainThreadTask(() => Change(3), "worldaudit-test-bob-transfer");
                    }

                    void Change(int slot)
                    {
                        try
                        {
                            inventory[slot].Itemstack = Stack("game:rock-granite", slot);
                            inventory.DidModifyItemSlot(inventory[slot]);
                            if (--remaining == 0)
                            {
                                alice.Player.InventoryManager.CloseInventory(inventory);
                                bob.Player.InventoryManager.CloseInventory(inventory);
                                finished.TrySetResult();
                            }
                        }
                        catch (Exception error) { finished.TrySetException(error); }
                    }
                }
                catch (Exception error) { finished.TrySetException(error); }
            }, "worldaudit-test-delayed-transfers");

            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await runtime.FlushAsync();
            var world = runtime.Options.WorldId!;
            var position = AuditPos(pos);
            var rows = await runtime.Repository.GetContainerHistoryAsync(new(world, position, Limit: 20));
            Require(rows.Count == 5, $"Expected 5 container changes, got {rows.Count}.");
            var bySlot = rows.ToDictionary(row => row.Lines.Single().SlotId!.Value);
            Require(bySlot[2].Actor.ExternalId == alice.Player.PlayerUID && bySlot[3].Actor.ExternalId == bob.Player.PlayerUID,
                "Deferred transfer inherited the wrong player or lost the initiating player.");
            Require(bySlot[4].Cause == AuditCause.Unknown && bySlot[4].Actor.Name == "#unknown",
                "Independent callback inherited a player's identity.");

            var filter = new LookupFilters(null, 1, alice.Player.PlayerName, AuditCause.Player, null, null,
                IncludeBlocks: false, IncludeContainers: true);
            var lookup = await runtime.ContainerQueries.LookupAsync(world, position, filter);
            Require(lookup.Count == 2 && lookup.Any(row => row.Id == bySlot[2].Id), "Player lookup missed the delayed transfer.");
            var preview = await runtime.RollbackPlanner.PreviewRollbackAsync(world, position, filter, new("ServerTests"), name);
            Require(preview.PlannedContainerCount == 2, "Player rollback preview missed or overselected container changes.");
            await runtime.RollbackCoordinator.CancelAsync(preview.Id);
            results.Add(new { name, passed = true });
        }
        catch (Exception error) { results.Add(new { name, passed = false, error = error.ToString() }); }
    }
}
