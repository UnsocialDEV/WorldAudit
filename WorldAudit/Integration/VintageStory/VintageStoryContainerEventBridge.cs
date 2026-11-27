using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Domain;
using WorldAudit.Integration;

namespace WorldAudit.Integration.VintageStory;

public sealed class VintageStoryContainerEventBridge
{
    private readonly ICoreServerAPI _api;
    private readonly IContainerTransactionCapture _capture;
    private readonly Func<bool> _containerAuditEnabled;
    private readonly Func<string> _worldIdAccessor;
    private readonly System.Func<string, bool> _isInspectEnabled;
    internal static VintageStoryContainerEventBridge? Current { get; private set; }
    private readonly BlockMutationScopeManager _scopes;
    private readonly Dictionary<string, Subscription> _trackedInventorySubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InventoryTrackingState> _inventoryStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ContainerSessionKey, ContainerVisit> _sessions = new();

    public VintageStoryContainerEventBridge(
        ICoreServerAPI api,
        IContainerTransactionCapture capture,
        Func<bool> containerAuditEnabled,
        Func<string> worldIdAccessor,
        System.Func<string, bool> isInspectEnabled,
        VintageStoryInteractionAttributionTracker interactionTracker,
        BlockMutationScopeManager? scopes = null)
    {
        _api = api;
        _capture = capture;
        _containerAuditEnabled = containerAuditEnabled;
        _worldIdAccessor = worldIdAccessor;
        _isInspectEnabled = isInspectEnabled;
        _scopes = scopes ?? new BlockMutationScopeManager();
    }

    public void Register()
    {
        Current = this;
        _api.Event.DidUseBlock += OnDidUseBlock;
        _api.Event.PlayerDisconnect += OnPlayerDisconnect;
    }

    public void Unregister()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        foreach (var subscription in _trackedInventorySubscriptions.Values.ToArray()) UntrackInventory(subscription.Inventory);
        _sessions.Clear();
        _inventoryStates.Clear();
        _api.Event.DidUseBlock -= OnDidUseBlock;
        _api.Event.PlayerDisconnect -= OnPlayerDisconnect;
    }

    private void OnDidUseBlock(IServerPlayer byPlayer, BlockSelection blockSel)
    {
        try
        {
            if (!_containerAuditEnabled() || byPlayer?.PlayerUID is null || blockSel?.Position is null)
            {
                return;
            }

            if (_isInspectEnabled(byPlayer.PlayerUID))
            {
                return;
            }

            if (_api.World.BlockAccessor.GetBlockEntity(blockSel.Position) is not IBlockEntityContainer container ||
                container.Inventory is not InventoryBase inventory)
            {
                return;
            }

            var metadata = CaptureInventoryMetadata(container, inventory, blockSel.Position.Copy());

            EnsureInventoryTracked(inventory, metadata);

        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Container interaction capture failed: {0}", exception);
        }
    }

    private void OnPlayerDisconnect(IServerPlayer byPlayer)
    {
        try
        {
            if (byPlayer?.PlayerUID is null)
            {
                return;
            }

            foreach (var session in _sessions.Keys.Where(key => string.Equals(key.PlayerUid, byPlayer.PlayerUID, StringComparison.OrdinalIgnoreCase)))
            {
                if (_sessions.TryRemove(session, out var state))
                {
                    PersistInventoryDelta(state.Inventory);
                }
            }
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Container disconnect cleanup failed: {0}", exception);
        }
    }

    private void EnsureInventoryTracked(InventoryBase inventory)
    {
        EnsureInventoryTracked(inventory, CaptureInventoryMetadata(null, inventory, inventory.Pos?.Copy()));
    }

    private void EnsureInventoryTracked(InventoryBase inventory, InventoryMetadata metadata)
    {
        var inventoryId = metadata.InventoryId;
        if (_trackedInventorySubscriptions.TryGetValue(inventoryId, out var existing))
        {
            if (ReferenceEquals(existing.Inventory, inventory)) return;
            UntrackInventory(existing.Inventory);
        }
        OnInventoryOpenedDelegate opened = player => OnInventoryOpened(inventory, player);
        OnInventoryClosedDelegate closed = player => OnInventoryClosed(inventory, player);
        Action<int> modified = slotId => OnSlotModified(inventory, slotId);
        inventory.OnInventoryOpened += opened;
        inventory.OnInventoryClosed += closed;
        inventory.SlotModified += modified;
        _trackedInventorySubscriptions[inventoryId] = new Subscription(inventory, opened, closed, modified);
        _inventoryStates[inventoryId] = CreateTrackingState(metadata, inventory);
    }

    internal InventoryBase[] TrackedInventories => _trackedInventorySubscriptions.Values.Select(s => s.Inventory).ToArray();

    internal void TrackAt(BlockPos position)
    {
        if (_containerAuditEnabled() && _api.World.BlockAccessor.GetBlockEntity(position) is IBlockEntityContainer container && container.Inventory is InventoryBase inventory)
            EnsureInventoryTracked(inventory, CaptureInventoryMetadata(container, inventory, position.Copy()));
    }

    internal void TrackInventory(InventoryBase inventory)
    {
        if (_containerAuditEnabled() && inventory.Pos is not null) EnsureInventoryTracked(inventory);
    }

    internal void InventoryDeserialized(InventoryBase inventory)
    {
        if (!_containerAuditEnabled() || inventory.Pos is null) return;
        var metadata = CaptureInventoryMetadata(null, inventory, inventory.Pos.Copy());
        if (_trackedInventorySubscriptions.TryGetValue(metadata.InventoryId, out var existing)
            && ReferenceEquals(existing.Inventory, inventory))
            _inventoryStates[metadata.InventoryId] = CreateTrackingState(metadata, inventory);
        else
            EnsureInventoryTracked(inventory, metadata);
    }

    internal void UntrackInventory(InventoryBase inventory)
    {
        var entry = _trackedInventorySubscriptions.FirstOrDefault(pair => ReferenceEquals(pair.Value.Inventory, inventory));
        if (entry.Value is null) return;
        inventory.OnInventoryOpened -= entry.Value.Opened;
        inventory.OnInventoryClosed -= entry.Value.Closed;
        inventory.SlotModified -= entry.Value.Modified;
        _trackedInventorySubscriptions.Remove(entry.Key);
        _inventoryStates.TryRemove(entry.Key, out _);
        foreach (var key in _sessions.Keys.Where(k => k.InventoryId == entry.Key)) _sessions.TryRemove(key, out _);
    }

    private sealed record Subscription(InventoryBase Inventory, OnInventoryOpenedDelegate Opened, OnInventoryClosedDelegate Closed, Action<int> Modified);

    private void OnInventoryOpened(InventoryBase inventory, IPlayer player)
    {
        try
        {
            if (!_containerAuditEnabled() || player?.PlayerUID is null)
            {
                return;
            }

            EnsureSessionForPlayer(inventory, player, inventory.Pos?.Copy());
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Container session open failed: {0}", exception);
        }
    }

    private void OnInventoryClosed(InventoryBase inventory, IPlayer player)
    {
        try
        {
            if (player?.PlayerUID is null)
            {
                return;
            }

            var inventoryId = ResolveInventoryId(inventory, inventory.Pos?.Copy());
            var key = new ContainerSessionKey(inventoryId, player.PlayerUID);
            if (_sessions.TryRemove(key, out var state))
            {
                PersistInventoryDelta(state.Inventory);
            }
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Container session close failed: {0}", exception);
        }
    }

    private void OnSlotModified(InventoryBase inventory, int slotId)
    {
        try
        {
            if (!_containerAuditEnabled())
            {
                return;
            }

            PersistInventoryDelta(inventory, slotId);
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Container slot modification capture failed: {0}", exception);
        }
    }

    private void EnsureSessionForPlayer(InventoryBase inventory, IPlayer player, BlockPos? position)
    {
        var inventoryId = ResolveInventoryId(inventory, position);
        if (string.IsNullOrWhiteSpace(inventoryId) || player.PlayerUID is null)
        {
            return;
        }

        EnsureInventoryTracked(inventory, CaptureInventoryMetadata(null, inventory, position));
        var key = new ContainerSessionKey(inventoryId, player.PlayerUID);
        _sessions.GetOrAdd(key, _ => new ContainerVisit(inventory, Guid.NewGuid().ToString("N")));
    }

    private void PersistInventoryDelta(InventoryBase inventory, int? modifiedSlot = null)
    {
        if (!_containerAuditEnabled()) return;
        var inventoryId = ResolveInventoryId(inventory, inventory.Pos?.Copy());
        if (!_inventoryStates.TryGetValue(inventoryId, out var state))
        {
            state = CreateTrackingState(CaptureInventoryMetadata(null, inventory, inventory.Pos?.Copy()), inventory);
            _inventoryStates[inventoryId] = state;
        }

        IReadOnlyList<SlotSnapshot> afterSlots;
        IReadOnlyList<ContainerTransactionLineCapture> lines;
        if (modifiedSlot is int id && id >= 0 && id < inventory.Count && inventory.Count == state.LastSlots.Count)
        {
            var after = CaptureSlot(inventory, id);
            var before = state.LastSlots[id];
            if (SnapshotsEqual(before, after)) return;
            var updated = state.LastSlots.ToArray();
            updated[id] = after;
            afterSlots = updated;
            lines = BuildLines([before], [after]);
        }
        else
        {
            afterSlots = CaptureSlots(inventory);
            lines = BuildLines(state.LastSlots, afterSlots);
        }
        if (lines.Count == 0) return;
        var snapshot = SerializeInventorySnapshot(afterSlots);
        _inventoryStates[inventoryId] = state with { LastSlots = afterSlots, LastSnapshot = snapshot };
        if (state.Position is not { } pos) return;
        // A closing inventory can expose older edits which did not raise SlotModified.
        var scope = modifiedSlot.HasValue ? _scopes.GetAmbientScope() : null;
        var actor = scope is { IsTrusted: true } ? scope : BlockMutationScope.Synthetic(AuditCause.Unknown);
        var visitId = actor.Cause == AuditCause.Player && actor.ActorExternalId is { } uid &&
            _sessions.TryGetValue(new ContainerSessionKey(inventoryId, uid), out var visit) ? visit.Id : null;
        _capture.Capture(new ContainerTransactionCaptureContext(state.WorldId,
            new BlockPosition(pos.X, pos.InternalY, pos.Z), DateTimeOffset.UtcNow, actor.ActorName, actor.ActorExternalId,
            actor.Cause, state.InventoryType, state.ContainerLabel, lines, state.LastSnapshot, snapshot,
            OperationId: modifiedSlot.HasValue ? VintageStoryGameHooks.ContainerOperationId : null, VisitId: visitId));
    }

    private string? ResolveContainerLabel(BlockPos? position)
    {
        if (position is null)
        {
            return null;
        }

        return _api.World.BlockAccessor.GetBlock(position)?.Code?.ToString();
    }

    private InventoryMetadata CaptureInventoryMetadata(IBlockEntityContainer? container, InventoryBase inventory, BlockPos? position)
    {
        position ??= inventory.Pos?.Copy();
        container ??= position is not null
            ? _api.World.BlockAccessor.GetBlockEntity(position) as IBlockEntityContainer
            : null;

        var inventoryType = ResolveInventoryType(container, inventory, position);
        return new InventoryMetadata(
            ResolveInventoryId(inventory, position),
            _worldIdAccessor(),
            position,
            inventoryType,
            ResolveContainerLabel(position));
    }

    private InventoryTrackingState CreateTrackingState(InventoryMetadata metadata, InventoryBase inventory)
    {
        var slots = CaptureSlots(inventory);
        return new InventoryTrackingState(
            metadata.InventoryId,
            metadata.WorldId,
            metadata.Position,
            metadata.InventoryType,
            metadata.ContainerLabel,
            slots,
            SerializeInventorySnapshot(slots));
    }

    private string ResolveInventoryType(IBlockEntityContainer? container, InventoryBase inventory, BlockPos? position)
    {
        if (!string.IsNullOrWhiteSpace(container?.InventoryClassName))
        {
            return container.InventoryClassName;
        }

        if (!string.IsNullOrWhiteSpace(inventory.ClassName))
        {
            return inventory.ClassName;
        }

        if (position is not null)
        {
            var blockCode = _api.World.BlockAccessor.GetBlock(position)?.Code?.ToString();
            if (!string.IsNullOrWhiteSpace(blockCode))
            {
                return blockCode;
            }

            var blockEntityType = _api.World.BlockAccessor.GetBlockEntity(position)?.GetType().Name;
            if (!string.IsNullOrWhiteSpace(blockEntityType))
            {
                return blockEntityType;
            }
        }

        return inventory.GetType().Name;
    }

    private string ResolveInventoryId(InventoryBase inventory, BlockPos? position)
    {
        if (!string.IsNullOrWhiteSpace(inventory.InventoryID))
        {
            return $"{inventory.InventoryID}:d{(position ?? inventory.Pos)?.dimension ?? 0}";
        }

        var actualPosition = position ?? inventory.Pos;
        if (actualPosition is not null)
        {
            return $"block:{actualPosition.X}:{actualPosition.InternalY}:{actualPosition.Z}";
        }

        return inventory.ClassName ?? inventory.GetType().FullName ?? "inventory";
    }

    private static IReadOnlyList<ContainerTransactionLineCapture> BuildLines(
        IReadOnlyList<SlotSnapshot> beforeSlots,
        IReadOnlyList<SlotSnapshot> afterSlots)
    {
        var count = Math.Max(beforeSlots.Count, afterSlots.Count);
        var lines = new List<ContainerTransactionLineCapture>(count);

        for (var slotId = 0; slotId < count; slotId++)
        {
            var before = slotId < beforeSlots.Count ? beforeSlots[slotId] : new SlotSnapshot(slotId, null, 0, null);
            var after = slotId < afterSlots.Count ? afterSlots[slotId] : new SlotSnapshot(slotId, null, 0, null);

            if (SnapshotsEqual(before, after))
            {
                continue;
            }

            if (before.ItemCode is not null && after.ItemCode is not null && before.ItemCode != after.ItemCode)
            {
                lines.Add(new ContainerTransactionLineCapture(before.ItemCode, before.SlotId, -before.Quantity, before.Quantity, 0, before.StackBytes, null));
                lines.Add(new ContainerTransactionLineCapture(after.ItemCode, after.SlotId, after.Quantity, 0, after.Quantity, null, after.StackBytes));
                continue;
            }

            var itemCode = after.ItemCode ?? before.ItemCode;
            if (string.IsNullOrWhiteSpace(itemCode))
            {
                continue;
            }

            lines.Add(new ContainerTransactionLineCapture(
                ItemCode: itemCode,
                SlotId: after.SlotId,
                QuantityDelta: after.Quantity - before.Quantity,
                BeforeQuantity: before.Quantity,
                AfterQuantity: after.Quantity,
                StackBeforeSnapshot: before.StackBytes,
                StackAfterSnapshot: after.StackBytes));
        }

        return lines;
    }

    private static bool SnapshotsEqual(SlotSnapshot before, SlotSnapshot after)
    {
        if (!string.Equals(before.ItemCode, after.ItemCode, StringComparison.OrdinalIgnoreCase) ||
            before.Quantity != after.Quantity)
        {
            return false;
        }

        return StructuralComparisons.StructuralEqualityComparer.Equals(before.StackBytes, after.StackBytes);
    }

    private IReadOnlyList<SlotSnapshot> CaptureSlots(InventoryBase inventory)
    {
        var snapshots = new List<SlotSnapshot>(inventory.Count);
        for (var slotId = 0; slotId < inventory.Count; slotId++)
        {
            snapshots.Add(CaptureSlot(inventory, slotId));
        }
        return snapshots;
    }

    private SlotSnapshot CaptureSlot(InventoryBase inventory, int slotId)
    {
        var stack = inventory[slotId]?.Itemstack;
        return new SlotSnapshot(slotId, stack?.Collectible?.Code?.ToString(), stack?.StackSize ?? 0,
            TrySerializeStack(stack, inventory, slotId));
    }

    private byte[]? TrySerializeStack(ItemStack? stack, InventoryBase inventory, int slotId)
    {
        if (stack is null)
        {
            return null;
        }

        try
        {
            return stack.ToBytes();
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Cannot serialize inventory {0}, slot {1} ({2}). Rollback for this snapshot is unavailable: {3}",
                inventory.InventoryID, slotId, stack.Collectible?.Code, exception);
            return null;
        }
    }

    private static byte[] SerializeInventorySnapshot(IReadOnlyList<SlotSnapshot> snapshots)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            writer.Write(snapshot.SlotId);
            writer.Write(snapshot.ItemCode ?? string.Empty);
            writer.Write(snapshot.Quantity);
            writer.Write(snapshot.StackBytes?.Length ?? 0);
            if (snapshot.StackBytes is not null)
            {
                writer.Write(snapshot.StackBytes);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    private sealed record ContainerVisit(InventoryBase Inventory, string Id);

    private sealed record ContainerSessionKey(string InventoryId, string PlayerUid);

    private sealed record InventoryTrackingState(
        string InventoryId,
        string WorldId,
        BlockPos? Position,
        string InventoryType,
        string? ContainerLabel,
        IReadOnlyList<SlotSnapshot> LastSlots,
        byte[] LastSnapshot);

    private sealed record InventoryMetadata(
        string InventoryId,
        string WorldId,
        BlockPos? Position,
        string InventoryType,
        string? ContainerLabel);

    private sealed record SlotSnapshot(int SlotId, string? ItemCode, int Quantity, byte[]? StackBytes);
}
