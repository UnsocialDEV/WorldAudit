using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration.VintageStory;

namespace WorldAudit.ServerTests;

public sealed partial class ServerTests
{
    private void RebuildPile()
    {
        var pos = Pos(534, 550, 102);
        var accessor = api.World.BlockAccessor;
        Clear(pos);
        accessor.SetBlock(Block("game:log-grown-oak-ud").Id, pos.EastCopy());
        accessor.SetBlock(Block("game:groundstorage").Id, pos);
        var pile = (BlockEntityGroundStorage)accessor.GetBlockEntity(pos);
        var slot = pile.Inventory[0];
        slot.Itemstack = Stack("game:firewood", 8);
        slot.Itemstack.Attributes.SetString("fixture", "marked wood");
        pile.DetermineStorageProperties(slot);
        pile.MeshAngle = 1.25f;
        slot.MarkDirty();
        AccessTools.Method(typeof(BlockEntityGroundStorage), "UpdateIgnitable").Invoke(pile, null);
        pile.TryIgnite();
        Require(pile.IsBurning, "Fixture pile did not ignite");
        pile.GetBehavior<BEBehaviorBurning>().OnFirePlaced(pos, pos, alice.Player.PlayerUID, false);
        Equip(alice, null);
        PlaceBreak(alice, pos, false);
        Require(accessor.GetBlock(pos).Id == 0, "Fixture pile was not broken");
        var rebuilt = Rollback(pos, BlockAuditAction.Break);
        VerifyPile();
        var undo = runtime.RollbackPlanner.PreviewUndoAsync(rebuilt.Id, new("ServerTests")).GetAwaiter().GetResult();
        var entry = runtime.Repository.GetRollbackBlockPlanEntriesAsync(undo.Id).GetAwaiter().GetResult().Single(e => e.Position == AuditPos(pos));
        runtime.RollbackCoordinator.CancelAsync(undo.Id).GetAwaiter().GetResult();
        var restoredStack = ((BlockEntityGroundStorage)accessor.GetBlockEntity(pos)).Inventory[0].Itemstack!;
        restoredStack.Attributes.SetString("fixture", "someone edited this stack");
        var conflict = new VintageStoryRollbackBlockApplier(api).ApplyAsync(entry).GetAwaiter().GetResult();
        Require(conflict.Status == RollbackBlockApplyStatus.Conflict && !conflict.ChangedWorld, "An edited pile could be overwritten");
        restoredStack.Attributes.SetString("fixture", "marked wood");
    }

    private void VerifyPile()
    {
        var pos = Pos(534, 550, 102);
        var accessor = api.World.BlockAccessor;
        var pile = accessor.GetBlockEntity(pos) as BlockEntityGroundStorage;
        Require(pile != null && !pile.IsBurning && !pile.GetBehavior<BEBehaviorBurning>().IsBurning, "Recovered pile is missing or burning");
        var stack = pile!.Inventory[0].Itemstack;
        Require(stack?.Collectible.Code.ToString() == "game:firewood" && stack.StackSize == 8 &&
            stack.Attributes.GetString("fixture") == "marked wood" && pile.MeshAngle == 1.25f, "Recovered pile contents or orientation changed");
        Require(accessor.GetBlock(pos.EastCopy()).Code.ToString() == "game:log-grown-oak-ud", "Adjacent log burned");
    }

    private void RebuildBomb()
    {
        var pos = Pos(550, 536, 105);
        var accessor = api.World.BlockAccessor;
        Clear(pos);
        // Raw granite breaks on a delayed neighbor update when suspended in air.
        var support = Block("game:log-grown-oak-ud");
        accessor.SetBlock(support.Id, pos.DownCopy(2));
        accessor.SetBlock(support.Id, pos.EastCopy().DownCopy());
        accessor.SetBlock(Block("game:rock-granite").Id, pos.EastCopy());
        accessor.SetBlock(Block("game:bomb-stone").Id, pos);
        var bomb = (BlockEntityBomb)accessor.GetBlockEntity(pos);
        var ignite = AccessTools.Method(typeof(BlockEntityBomb), "OnIgnite");
        ignite.Invoke(bomb, [alice.Player]);
        var armed = new TreeAttribute();
        bomb.ToTreeAttributes(armed);
        var worldId = runtime.Options.WorldId!;
        runtime.Repository.WriteBlockEventsAsync([new(null, worldId, AuditPos(pos), DateTimeOffset.UtcNow,
            new("BombFixture"), AuditCause.Player, BlockAuditAction.Place, "game:air", "game:bomb-stone",
            NewBlockEntitySnapshot: armed.ToBytes())]).GetAwaiter().GetResult();
        AccessTools.Method(typeof(BlockEntityBomb), "Combust").Invoke(bomb, [0.1f]);
        Require(accessor.GetBlock(pos).Id == 0 && accessor.GetBlock(pos.EastCopy()).Id == 0, "Fixture blast did not remove both blocks");
        var damage = new LookupFilters(null, 5, alice.Player.PlayerName, AuditCause.Explosion, null, null, IncludeContainers: false);
        var rebuilt = Apply(runtime.RollbackPlanner.PreviewRollbackAsync(worldId, AuditPos(pos), damage, new("ServerTests"), "blast").GetAwaiter().GetResult());
        VerifyBomb();

        // Replay the blast against inert output, then undo only what that job actually removed.
        var replay = Apply(runtime.RollbackPlanner.PreviewRestoreAsync(worldId, AuditPos(pos), damage, new("ServerTests"), "blast").GetAwaiter().GetResult());
        Undo(replay);
        VerifyBomb();

        // Undo a job that actually removed an armed bomb. Its before-snapshot must retain the igniter.
        ignite.Invoke(accessor.GetBlockEntity(pos), [alice.Player]);
        var inverse = runtime.RollbackPlanner.PreviewUndoAsync(rebuilt.Id, new("ServerTests")).GetAwaiter().GetResult();
        var bombEntry = runtime.Repository.GetRollbackBlockPlanEntriesAsync(inverse.Id).GetAwaiter().GetResult().Single(e => e.Position == AuditPos(pos));
        runtime.RollbackCoordinator.CancelAsync(inverse.Id).GetAwaiter().GetResult();
        var conflict = new VintageStoryRollbackBlockApplier(api).ApplyAsync(bombEntry).GetAwaiter().GetResult();
        Require(conflict.Status == RollbackBlockApplyStatus.Conflict && !conflict.ChangedWorld, "Undo accepted a newly armed bomb");
        replay = Apply(runtime.RollbackPlanner.PreviewRestoreAsync(worldId, AuditPos(pos), damage, new("ServerTests"), "armed blast").GetAwaiter().GetResult());
        var before = new TreeAttribute();
        before.FromBytes(History(pos).Single(e => e.SourceJobId == replay.Id).OldBlockEntitySnapshot);
        Require(before.GetInt("lit") == 1 && before.GetString("ignitedByPlayerUid") == alice.Player.PlayerUID, "Job history lost ignition evidence");
        Undo(replay);
        VerifyBomb();
        accessor.SetBlock(0, pos);
        var placement = damage with { ActorName = "BombFixture", Cause = AuditCause.Player };
        Apply(runtime.RollbackPlanner.PreviewRestoreAsync(worldId, AuditPos(pos), placement, new("ServerTests"), "lit placement").GetAwaiter().GetResult());
        VerifyBomb();
    }

    private void VerifyBomb()
    {
        var pos = Pos(550, 536, 105);
        var accessor = api.World.BlockAccessor;
        var bomb = accessor.GetBlockEntity(pos) as BlockEntityBomb;
        Require(bomb != null && !bomb.IsLit && accessor.GetBlock(pos).Code.ToString() == "game:bomb-stone", "Restored bomb is missing, armed or the wrong variant");
        AccessTools.Method(typeof(BlockEntityBomb), "OnTick").Invoke(bomb, [60f]);
        Require(accessor.GetBlockEntity(pos) == bomb && accessor.GetBlock(pos.EastCopy()).Code.ToString() == "game:rock-granite",
            "Restored bomb damaged the rebuilt wall");
    }

    private void VerifyRebuiltHazards()
    {
        VerifyPile();
        VerifyBomb();
    }
}
