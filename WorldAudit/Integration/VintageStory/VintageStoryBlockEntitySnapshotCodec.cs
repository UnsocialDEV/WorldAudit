using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

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

        try
        {
            var tree = new TreeAttribute();
            blockEntity.ToTreeAttributes(tree);
            return TryGetOrNull(() => SerializerUtil.Serialize(tree));
        }
        catch
        {
            return null;
        }
    }

    public bool TryRestore(BlockPos pos, byte[] snapshot, out string? error)
    {
        error = null;

        TreeAttribute tree;
        try
        {
            tree = SerializerUtil.Deserialize<TreeAttribute>(snapshot);
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
            blockEntity.FromTreeAttributes(tree, _world);
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

    internal static T? TryGetOrNull<T>(System.Func<T> operation)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return operation();
        }
        catch
        {
            return null;
        }
    }
}
