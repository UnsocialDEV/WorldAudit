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
    private readonly VintageStoryInteractionAttributionTracker _interactionTracker;
    internal static VintageStoryContainerEventBridge? Current { get; private set; }
    private readonly BlockMutationScopeManager _scopes;
    private readonly Dictionary<string, Subscription> _trackedInventorySubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InventoryTrackingState> _inventoryStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ContainerSessionKey, ContainerSessionState> _sessions = new();

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
        _interactionTracker = interactionTracker;
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
            RecordInteraction(byPlayer, metadata.Position);
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
                    PersistSession(state);
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
        if (_api.World.BlockAccessor.GetBlockEntity(position) is IBlockEntityContainer container && container.Inventory is InventoryBase inventory)
            EnsureInventoryTracked(inventory, CaptureInventoryMetadata(container, inventory, position.Copy()));
    }

    internal void TrackInventory(InventoryBase inventory)
    {
        if (inventory.Pos is not null) EnsureInventoryTracked(inventory);
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

            RecordInteraction(player, inventory.Pos?.Copy());
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
                PersistSession(state);
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

            PersistInventoryDelta(inventory);
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
        var snapshots = CaptureSlots(inventory);
        var metadata = CaptureInventoryMetadata(null, inventory, position);
        var state = new ContainerSessionState(
            Inventory: inventory,
            InventoryId: inventoryId,
            WorldId: metadata.WorldId,
            Position: metadata.Position,
            InventoryType: metadata.InventoryType,
            ContainerLabel: metadata.ContainerLabel,
            ActorName: player.PlayerName,
            ActorExternalId: player.PlayerUID,
            BeforeSlots: snapshots,
            BeforeSnapshot: SerializeInventorySnapshot(snapshots),
            WasAmbiguous: CountSessionsForInventory(inventoryId) > 0);

        _sessions.AddOrUpdate(key, state, (_, existing) => existing with
        {
            Position = existing.Position ?? state.Position,
            InventoryType = existing.InventoryType,
            ContainerLabel = existing.ContainerLabel ?? state.ContainerLabel,
            WasAmbiguous = existing.WasAmbiguous || state.WasAmbiguous
        });
    }

    private void PersistSession(ContainerSessionState state)
    {
        var position = state.Position;
        if (!_containerAuditEnabled() || position is null)
        {
            return;
        }

        var afterSlots = CaptureSlots(state.Inventory);
        var lines = BuildLines(state.BeforeSlots, afterSlots);
        if (lines.Count == 0)
        {
            return;
        }

        PersistTransaction(
            state.InventoryId,
            state.WorldId,
            position,
            state.InventoryType,
            state.ContainerLabel,
            state.BeforeSlots,
            state.BeforeSnapshot,
            afterSlots,
            lines,
            state.ActorName,
            state.ActorExternalId,
            state.WasAmbiguous);
    }

    private void PersistInventoryDelta(InventoryBase inventory)
    {
        var inventoryId = ResolveInventoryId(inventory, inventory.Pos?.Copy());
        if (!_inventoryStates.TryGetValue(inventoryId, out var state))
        {
            state = CreateTrackingState(CaptureInventoryMetadata(null, inventory, inventory.Pos?.Copy()), inventory);
            _inventoryStates[inventoryId] = state;
        }

        var afterSlots = CaptureSlots(inventory);
        var lines = BuildLines(state.LastSlots, afterSlots);
        if (lines.Count == 0)
        {
            return;
        }

        if (state.Position is null)
        {
            _inventoryStates[inventoryId] = state with
            {
                LastSlots = afterSlots,
                LastSnapshot = SerializeInventorySnapshot(afterSlots)
            };
            return;
        }

        PersistTransaction(
            inventoryId,
            state.WorldId,
            state.Position,
            state.InventoryType,
            state.ContainerLabel,
            state.LastSlots,
            state.LastSnapshot,
            afterSlots,
            lines,
            null,
            null,
            false);

        var afterSnapshot = SerializeInventorySnapshot(afterSlots);
        _inventoryStates[inventoryId] = state with
        {
            LastSlots = afterSlots,
            LastSnapshot = afterSnapshot
        };

        foreach (var entry in _sessions.Where(pair => string.Equals(pair.Key.InventoryId, inventoryId, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _sessions[entry.Key] = entry.Value with
            {
                BeforeSlots = afterSlots,
                BeforeSnapshot = afterSnapshot,
                WasAmbiguous = entry.Value.WasAmbiguous || CountSessionsForInventory(inventoryId) > 1
            };
        }
    }

    private void PersistTransaction(
        string inventoryId,
        string worldId,
        BlockPos position,
        string inventoryType,
        string? containerLabel,
        IReadOnlyList<SlotSnapshot> beforeSlots,
        byte[] beforeSnapshot,
        IReadOnlyList<SlotSnapshot> afterSlots,
        IReadOnlyList<ContainerTransactionLineCapture> lines,
        string? fallbackActorName,
        string? fallbackActorExternalId,
        bool fallbackIsAmbiguous)
    {
        var attribution = ResolveAttribution(worldId, position, inventoryId, fallbackActorName, fallbackActorExternalId, fallbackIsAmbiguous);
        _capture.Capture(new ContainerTransactionCaptureContext(
            WorldId: worldId,
            Position: new BlockPosition(position.X, position.InternalY, position.Z),
            OccurredAt: DateTimeOffset.UtcNow,
            ActorName: attribution.ActorName,
            ActorExternalId: attribution.ActorExternalId,
            Cause: attribution.Cause,
            InventoryType: inventoryType,
            ContainerLabel: containerLabel,
            Lines: lines,
            BeforeSnapshot: beforeSnapshot,
            AfterSnapshot: SerializeInventorySnapshot(afterSlots)));
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

    private static InventoryTrackingState CreateTrackingState(InventoryMetadata metadata, InventoryBase inventory)
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

    private void RecordInteraction(IPlayer player, BlockPos? position)
    {
        if (position is null || player?.PlayerUID is null)
        {
            return;
        }

        _interactionTracker.Record(_worldIdAccessor(), position, player.PlayerName, player.PlayerUID);
    }

    private Attribution ResolveAttribution(
        string worldId,
        BlockPos position,
        string inventoryId,
        string? fallbackActorName,
        string? fallbackActorExternalId,
        bool fallbackIsAmbiguous)
    {
        var scope = _scopes.GetAmbientScope();
        return scope is { IsTrusted: true }
            ? new Attribution(scope.ActorName, scope.ActorExternalId, scope.Cause)
            : new Attribution("#unknown", null, AuditCause.Unknown);
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
                lines.Add(new ContainerTransactionLineCapture(before.ItemCode, slotId, -before.Quantity, before.Quantity, 0, before.StackBytes, null));
                lines.Add(new ContainerTransactionLineCapture(after.ItemCode, slotId, after.Quantity, 0, after.Quantity, null, after.StackBytes));
                continue;
            }

            var itemCode = after.ItemCode ?? before.ItemCode;
            if (string.IsNullOrWhiteSpace(itemCode))
            {
                continue;
            }

            lines.Add(new ContainerTransactionLineCapture(
                ItemCode: itemCode,
                SlotId: slotId,
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

    private static IReadOnlyList<SlotSnapshot> CaptureSlots(InventoryBase inventory)
    {
        var snapshots = new List<SlotSnapshot>(inventory.Count);
        for (var slotId = 0; slotId < inventory.Count; slotId++)
        {
            var slot = inventory[slotId];
            var stack = slot?.Itemstack;
            snapshots.Add(new SlotSnapshot(
                SlotId: slotId,
                ItemCode: stack?.Collectible?.Code?.ToString(),
                Quantity: stack?.StackSize ?? 0,
                StackBytes: TrySerializeStack(stack)));
        }

        return snapshots;
    }

    private static byte[]? TrySerializeStack(ItemStack? stack)
    {
        if (stack is null)
        {
            return null;
        }

        try
        {
            return stack.ToBytes();
        }
        catch
        {
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

    private int CountSessionsForInventory(string? inventoryId)
    {
        if (string.IsNullOrWhiteSpace(inventoryId))
        {
            return 0;
        }

        return _sessions.Keys.Count(key => string.Equals(key.InventoryId, inventoryId, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ContainerSessionKey(string InventoryId, string PlayerUid);

    private sealed record ContainerSessionState(
        InventoryBase Inventory,
        string InventoryId,
        string WorldId,
        BlockPos? Position,
        string InventoryType,
        string? ContainerLabel,
        string ActorName,
        string? ActorExternalId,
        IReadOnlyList<SlotSnapshot> BeforeSlots,
        byte[] BeforeSnapshot,
        bool WasAmbiguous);

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

    private sealed record Attribution(string ActorName, string? ActorExternalId, AuditCause Cause);

    private sealed record SlotSnapshot(int SlotId, string? ItemCode, int Quantity, byte[]? StackBytes);
}
