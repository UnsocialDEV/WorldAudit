using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Application.Abstractions;
using WorldAudit.Domain;

namespace WorldAudit.Integration.VintageStory;

public sealed class VintageStoryRollbackContainerApplier : IRollbackContainerApplier
{
    private readonly ICoreServerAPI _api;

    public VintageStoryRollbackContainerApplier(ICoreServerAPI api)
    {
        _api = api;
    }

    public Task<RollbackContainerApplyResult> ApplyAsync(RollbackContainerPlanEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pos = new BlockPos(entry.Position.X, entry.Position.Y, entry.Position.Z);
        if (_api.World.BlockAccessor.GetChunkAtBlockPos(pos) is null)
        {
            return Task.FromResult(new RollbackContainerApplyResult(
                RollbackContainerApplyStatus.Conflict,
                false,
                null,
                "Target chunk is not loaded."));
        }

        if (_api.World.BlockAccessor.GetBlockEntity(pos) is not IBlockEntityContainer container
            || container.Inventory is not InventoryBase inventory)
        {
            return Task.FromResult(new RollbackContainerApplyResult(
                RollbackContainerApplyStatus.Conflict,
                false,
                null,
                "Target container inventory is not available."));
        }

        if (entry.TargetSnapshot is null)
        {
            return Task.FromResult(new RollbackContainerApplyResult(
                RollbackContainerApplyStatus.Failed,
                false,
                null,
                "Rollback container snapshot is missing."));
        }

        var previousSnapshot = SerializeInventorySnapshot(inventory);
        if (entry.ExpectedSnapshot is not null && !SnapshotsEqual(previousSnapshot, entry.ExpectedSnapshot))
        {
            return Task.FromResult(new RollbackContainerApplyResult(
                RollbackContainerApplyStatus.Conflict,
                false,
                previousSnapshot,
                "Current container state no longer matches the expected rollback snapshot."));
        }

        if (!TryPrepareSnapshot(inventory, entry.TargetSnapshot, out var preparedSnapshot, out var prepareStatus, out var prepareError))
        {
            return Task.FromResult(new RollbackContainerApplyResult(
                prepareStatus,
                false,
                previousSnapshot,
                prepareError));
        }

        var applySucceeded = TryApplySnapshot(inventory, container, preparedSnapshot, out var applyError, out var mutationAttempted);
        if (applySucceeded)
        {
            var changedWorld = !SnapshotsEqual(previousSnapshot, SerializeInventorySnapshot(inventory));
            return Task.FromResult(new RollbackContainerApplyResult(
                RollbackContainerApplyStatus.Applied,
                changedWorld,
                previousSnapshot));
        }

        if (!mutationAttempted)
        {
            return Task.FromResult(new RollbackContainerApplyResult(
                RollbackContainerApplyStatus.Failed,
                false,
                previousSnapshot,
                applyError));
        }

        string? revertApplyError = null;
        var reverted = TryPrepareSnapshot(inventory, previousSnapshot, out var preparedPreviousSnapshot, out _, out var revertPrepareError)
            && TryApplySnapshot(inventory, container, preparedPreviousSnapshot, out revertApplyError, out _)
            && SnapshotsEqual(previousSnapshot, SerializeInventorySnapshot(inventory));

        if (!reverted)
        {
            var revertError = revertPrepareError ?? revertApplyError ?? "Container state still differs from its pre-rollback value.";
            var error = $"{applyError} Revert failed: {revertError}";
            _api.Logger.Error("[WorldAudit] Failed to revert container rollback mutation at {0}: {1}", pos, error);
            return Task.FromResult(new RollbackContainerApplyResult(
                RollbackContainerApplyStatus.Failed,
                !SnapshotsEqual(previousSnapshot, SerializeInventorySnapshot(inventory)),
                previousSnapshot,
                error));
        }

        return Task.FromResult(new RollbackContainerApplyResult(
            RollbackContainerApplyStatus.Failed,
            false,
            previousSnapshot,
            applyError));
    }

    private bool TryPrepareSnapshot(
        InventoryBase inventory,
        byte[] snapshotBytes,
        out PreparedSnapshot preparedSnapshot,
        out RollbackContainerApplyStatus status,
        out string? error)
    {
        preparedSnapshot = new PreparedSnapshot([]);
        error = null;
        try
        {
            var slots = ReadSnapshot(snapshotBytes);
            if (slots.Count != inventory.Count)
            {
                status = RollbackContainerApplyStatus.Conflict;
                error = $"Snapshot slot count {slots.Count} does not match the current inventory size {inventory.Count}.";
                return false;
            }

            var preparedSlots = new PreparedSlot[slots.Count];
            var seenSlots = new HashSet<int>();
            for (var index = 0; index < slots.Count; index++)
            {
                var slot = slots[index];
                if (slot.SlotId < 0 || slot.SlotId >= inventory.Count)
                {
                    error = $"Snapshot slot {slot.SlotId} is outside the current inventory bounds.";
                    status = RollbackContainerApplyStatus.Failed;
                    return false;
                }

                if (!seenSlots.Add(slot.SlotId))
                {
                    error = $"Snapshot slot {slot.SlotId} is duplicated.";
                    status = RollbackContainerApplyStatus.Failed;
                    return false;
                }

                var itemStack = DecodeItemStack(slot.StackBytes, out error);
                if (error is not null)
                {
                    status = RollbackContainerApplyStatus.Failed;
                    return false;
                }

                preparedSlots[slot.SlotId] = new PreparedSlot(slot.SlotId, itemStack);
            }

            preparedSnapshot = new PreparedSnapshot(preparedSlots);
            status = RollbackContainerApplyStatus.Applied;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            status = RollbackContainerApplyStatus.Failed;
            return false;
        }
    }

    private bool TryApplySnapshot(
        InventoryBase inventory,
        IBlockEntityContainer container,
        PreparedSnapshot snapshot,
        out string? error,
        out bool mutationAttempted)
    {
        error = null;
        mutationAttempted = false;
        try
        {
            for (var slotId = 0; slotId < snapshot.Slots.Length; slotId++)
            {
                var preparedSlot = snapshot.Slots[slotId];
                var itemSlot = inventory[preparedSlot.SlotId];
                mutationAttempted = true;
                itemSlot.Itemstack = preparedSlot.ItemStack;
                inventory.DidModifyItemSlot(itemSlot);
                inventory.MarkSlotDirty(preparedSlot.SlotId);
            }

            container.CheckInventoryClearedMidTick();
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private ItemStack? DecodeItemStack(byte[]? stackBytes, out string? error)
    {
        error = null;
        if (stackBytes is null || stackBytes.Length == 0)
        {
            return null;
        }

        var stack = new ItemStack(stackBytes);
        if (!stack.ResolveBlockOrItem(_api.World))
        {
            error = "Could not resolve an item stack from the stored rollback snapshot.";
            return null;
        }

        return stack;
    }

    private static byte[] SerializeInventorySnapshot(InventoryBase inventory)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(inventory.Count);
        for (var slotId = 0; slotId < inventory.Count; slotId++)
        {
            var itemStack = inventory[slotId].Itemstack;
            writer.Write(slotId);
            writer.Write(itemStack?.Collectible?.Code?.ToString() ?? string.Empty);
            writer.Write(itemStack?.StackSize ?? 0);
            var stackBytes = itemStack?.ToBytes();
            writer.Write(stackBytes?.Length ?? 0);
            if (stackBytes is not null)
            {
                writer.Write(stackBytes);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static List<SnapshotSlot> ReadSnapshot(byte[] snapshotBytes)
    {
        using var stream = new MemoryStream(snapshotBytes, writable: false);
        using var reader = new BinaryReader(stream);
        var count = reader.ReadInt32();
        var result = new List<SnapshotSlot>(count);
        for (var index = 0; index < count; index++)
        {
            var slotId = reader.ReadInt32();
            _ = reader.ReadString();
            _ = reader.ReadInt32();
            var stackLength = reader.ReadInt32();
            var stackBytes = stackLength > 0 ? reader.ReadBytes(stackLength) : null;
            result.Add(new SnapshotSlot(slotId, stackBytes));
        }

        return result;
    }

    private static bool SnapshotsEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private sealed record SnapshotSlot(int SlotId, byte[]? StackBytes);

    private readonly record struct PreparedSlot(int SlotId, ItemStack? ItemStack);

    private sealed record PreparedSnapshot(PreparedSlot[] Slots);
}
