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
    private readonly ConcurrentDictionary<string, byte> _trackedInventorySubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InventoryTrackingState> _inventoryStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ContainerSessionKey, ContainerSessionState> _sessions = new();

    public VintageStoryContainerEventBridge(
        ICoreServerAPI api,
        IContainerTransactionCapture capture,
        Func<bool> containerAuditEnabled,
        Func<string> worldIdAccessor,
        System.Func<string, bool> isInspectEnabled,
        VintageStoryInteractionAttributionTracker interactionTracker)
    {
        _api = api;
        _capture = capture;
        _containerAuditEnabled = containerAuditEnabled;
        _worldIdAccessor = worldIdAccessor;
        _isInspectEnabled = isInspectEnabled;
        _interactionTracker = interactionTracker;
    }

    public void Register()
    {
        _api.Event.DidUseBlock += OnDidUseBlock;
        _api.Event.PlayerDisconnect += OnPlayerDisconnect;
    }

    public void Unregister()
    {
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
            EnsureSessionForPlayer(inventory, byPlayer, blockSel.Position.Copy());
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
        if (!_trackedInventorySubscriptions.TryAdd(inventoryId, 1))
        {
            _inventoryStates.AddOrUpdate(inventoryId, _ => CreateTrackingState(metadata, inventory), (_, existing) => existing with
            {
                Position = existing.Position ?? metadata.Position,
                InventoryType = existing.InventoryType,
                ContainerLabel = existing.ContainerLabel ?? metadata.ContainerLabel,
                LastSlots = existing.LastSlots,
                LastSnapshot = existing.LastSnapshot
            });
            return;
        }

        inventory.OnInventoryOpened += player => OnInventoryOpened(inventory, player);
        inventory.OnInventoryClosed += player => OnInventoryClosed(inventory, player);
        inventory.SlotModified += slotId => OnSlotModified(inventory, slotId);
        _inventoryStates[inventoryId] = CreateTrackingState(metadata, inventory);
    }

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
            Position: new BlockPosition(position.X, position.Y, position.Z),
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
            return inventory.InventoryID;
        }

        var actualPosition = position ?? inventory.Pos;
        if (actualPosition is not null)
        {
            return $"block:{actualPosition.X}:{actualPosition.Y}:{actualPosition.Z}";
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
        if (_interactionTracker.TryResolve(worldId, position, out var interaction) &&
            interaction is not null &&
            !interaction.IsAmbiguous)
        {
            return new Attribution(interaction.ActorName, interaction.ActorExternalId, AuditCause.Player);
        }

        var sessionActors = _sessions
            .Where(pair => string.Equals(pair.Key.InventoryId, inventoryId, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .DistinctBy(state => state.ActorExternalId ?? state.ActorName)
            .ToArray();

        if (sessionActors.Length == 1)
        {
            return new Attribution(sessionActors[0].ActorName, sessionActors[0].ActorExternalId, AuditCause.Player);
        }

        if (!fallbackIsAmbiguous && !string.IsNullOrWhiteSpace(fallbackActorName))
        {
            return new Attribution(fallbackActorName, fallbackActorExternalId, AuditCause.Player);
        }

        return new Attribution("#unknown", null, AuditCause.Unknown);
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
