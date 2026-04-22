using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace WorldAudit.Integration.VintageStory;

internal static class WorldAuditBlockMutationBehaviorRuntime
{
    private static VintageStoryBlockEventBridge? _bridge;

    public static void Initialize(VintageStoryBlockEventBridge bridge)
    {
        _bridge = bridge;
    }

    public static void Reset()
    {
        _bridge = null;
    }

    public static void OnBlockPlaced(Block block, IWorldAccessor world, BlockPos pos)
    {
        _bridge?.OnObservedBlockPlaced(block, world, pos);
    }

    public static void OnBlockRemoved(Block block, IWorldAccessor world, BlockPos pos)
    {
        _bridge?.OnObservedBlockRemoved(block, world, pos);
    }

    public static bool OnBlockInteractStart(IPlayer player, BlockSelection blockSelection)
    {
        return _bridge?.OnObservedBlockInteractStart(player, blockSelection) ?? false;
    }
}
