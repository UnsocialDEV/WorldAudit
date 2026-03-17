using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WorldAudit.Integration.VintageStory;

internal static class WorldAuditBlockMutationBehaviorRuntime
{
    private static VintageStoryBlockMutationObserver? _observer;

    public static void Initialize(VintageStoryBlockMutationObserver observer)
    {
        _observer = observer;
    }

    public static void Reset()
    {
        _observer = null;
    }

    public static void OnBlockPlaced(Block block, IWorldAccessor world, BlockPos pos)
    {
        _observer?.OnBlockPlaced(block, world, pos);
    }

    public static void OnBlockRemoved(Block block, IWorldAccessor world, BlockPos pos)
    {
        _observer?.OnBlockRemoved(block, world, pos);
    }
}
