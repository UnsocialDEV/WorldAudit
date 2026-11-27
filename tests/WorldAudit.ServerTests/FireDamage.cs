using HarmonyLib;
using Vintagestory.GameContent;
using WorldAudit.Application.Services;
using WorldAudit.Domain;

namespace WorldAudit.ServerTests;

public sealed partial class ServerTests
{
    private void FireDamage()
    {
        var fuel = Pos(556, 554, 95);
        var flame = fuel.UpCopy();
        var source = fuel.AddCopy(-8, 1, 0);
        Clear(fuel);
        Clear(source);
        var accessor = api.World.BlockAccessor;
        var wood = Block("game:log-grown-oak-ud");
        accessor.SetBlock(wood.Id, fuel);
        accessor.SetBlock(wood.Id, source.DownCopy());
        accessor.SetBlock(Block("game:fire").Id, source);
        var origin = accessor.GetBlockEntity(source).GetBehavior<BEBehaviorBurning>();
        origin.OnFirePlaced(source, source.DownCopy(), alice.Player.PlayerUID, false);
        AccessTools.Method(typeof(BEBehaviorBurning), "SpreadTo").Invoke(origin, [flame, fuel, null]);
        origin.KillFire(false);

        var burning = accessor.GetBlockEntity(flame).GetBehavior<BEBehaviorBurning>();
        burning.AllowFireSpread = false;
        var tick = AccessTools.Method(typeof(BEBehaviorBurning), "OnTick");
        for (var i = 0; i < 20; i++) tick.Invoke(burning, [0.1f]);
        burning.KillFire(true);
        Require(accessor.GetBlock(fuel).Id == 0, "Fire did not consume the wood");
        var rows = History(flame).Where(e => e.Cause == AuditCause.Fire).ToArray();
        Require(rows.Length == 2 && rows[0].Action == BlockAuditAction.Break && rows[1].Action == BlockAuditAction.Place,
            "Expected only fire placement and removal in inspect");
        var filters = new LookupFilters(null, 2, alice.Player.PlayerName, AuditCause.Fire, null, null, IncludeContainers: false);
        var plan = runtime.RollbackPlanner.PreviewRollbackAsync(runtime.Options.WorldId!, AuditPos(fuel), filters,
            new("ServerTests"), "fire damage").GetAwaiter().GetResult();
        Require(plan.PlannedBlockCount == 1, $"Expected only the burned log, got {plan.PlannedBlockCount} changes");
        Apply(plan);
        Require(accessor.GetBlock(fuel).Id == wood.Id && accessor.GetBlock(flame).Id == 0, "Fire rollback did not restore the wood and clear the flame");
        Require(!History(flame).Any(e => e.SourceJobId == plan.Id), "Rollback changed the flame position");
    }
}
