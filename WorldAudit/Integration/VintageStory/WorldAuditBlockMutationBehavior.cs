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
}
