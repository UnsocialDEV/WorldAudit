using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WorldAudit.Integration.VintageStory;

public sealed class WorldAuditBlockMutationBehavior : BlockBehavior
{
    public WorldAuditBlockMutationBehavior(Block block) : base(block)
    {
    }

    public override void OnBlockPlaced(IWorldAccessor world, BlockPos pos, ref EnumHandling handling)
    {
        WorldAuditBlockMutationBehaviorRuntime.OnBlockPlaced(block, world, pos);
    }

    public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos, ref EnumHandling handling)
    {
        WorldAuditBlockMutationBehaviorRuntime.OnBlockRemoved(block, world, pos);
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
    {
        if (WorldAuditBlockMutationBehaviorRuntime.OnBlockInteractStart(byPlayer, blockSel))
        {
            handling = EnumHandling.PreventSubsequent;
            return true;
        }

        return base.OnBlockInteractStart(world, byPlayer, blockSel, ref handling);
    }
}
