using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using HarmonyLib;

namespace WorldAudit.Integration.VintageStory;

public sealed class VintageStoryBlockEntitySnapshotCodec
{
    private readonly IWorldAccessor _world;

    public VintageStoryBlockEntitySnapshotCodec(IWorldAccessor world)
    {
        _world = world;
    }

    public byte[]? Capture(BlockPos pos)
    {
        var blockEntity = _world.BlockAccessor.GetBlockEntity(pos);
        if (blockEntity is null)
        {
            return null;
        }

        var tree = new TreeAttribute();
        blockEntity.ToTreeAttributes(tree);
        return tree.ToBytes();
    }

    public bool TryRestore(BlockPos pos, byte[] snapshot, out string? error)
    {
        error = null;

        TreeAttribute tree;
        try
        {
            tree = new TreeAttribute();
            tree.FromBytes(snapshot);
        }
        catch (Exception exception)
        {
            error = $"Failed to deserialize block entity snapshot: {exception.Message}";
            return false;
        }

        var blockEntity = _world.BlockAccessor.GetBlockEntity(pos);
        if (blockEntity is null)
        {
            error = "Target block entity is missing after block placement.";
            return false;
        }

        try
        {
            // 1.22.7 reads these lists only when both keys exist, but omits empty lists when saving.
            if (blockEntity.Behaviors.Any(b => b.GetType().FullName == "Vintagestory.GameContent.BEBehaviorMicroblockSnowCover"))
                foreach (var key in new[] { "snowcuboids", "groundSnowCuboids" })
                    if (!tree.HasAttribute(key)) tree[key] = new IntArrayAttribute(Array.Empty<int>());
            blockEntity.FromTreeAttributes(tree, _world);
            // Door FromTreeAttributes refreshes hitboxes only on the client in 1.22.7.
            foreach (var door in blockEntity.Behaviors.Where(b => b.GetType().FullName == "Vintagestory.GameContent.BEBehaviorDoor"))
            {
                AccessTools.Field(door.GetType(), "boxesClosed").SetValue(door, blockEntity.Block.CollisionBoxes);
                AccessTools.Method(door.GetType(), "SetupRotationsAndColSelBoxes").Invoke(door, [false]);
            }
            if (!TryValidateBlockEntity(blockEntity, out error))
            {
                return false;
            }

            blockEntity.MarkDirty(true);
            return true;
        }
        catch (Exception exception)
        {
            error = $"Failed to restore block entity snapshot: {exception.Message}";
            return false;
        }
    }

    public bool TryValidate(BlockPos pos, out string? error)
    {
        error = null;

        var blockEntity = _world.BlockAccessor.GetBlockEntity(pos);
        if (blockEntity is null)
        {
            return true;
        }

        return TryValidateBlockEntity(blockEntity, out error);
    }

    public bool AreEquivalent(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left.AsSpan().SequenceEqual(right)) return true;
        try
        {
            var leftTree = new TreeAttribute(); leftTree.FromBytes(left);
            var rightTree = new TreeAttribute(); rightTree.FromBytes(right);
            return leftTree.Equals(_world, rightTree);
        }
        catch { return false; }
    }

    private static bool TryValidateBlockEntity(BlockEntity blockEntity, out string? error)
    {
        error = null;

        try
        {
            var validationTree = new TreeAttribute();
            blockEntity.ToTreeAttributes(validationTree);
            return true;
        }
        catch (Exception exception)
        {
            error = $"Restored block entity state is invalid: {exception.Message}";
            return false;
        }
    }

}
