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
    private readonly ConcurrentDictionary<string, byte> _trackedInventories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ContainerSessionKey, ContainerSessionState> _sessions = new();

    public VintageStoryContainerEventBridge(
        ICoreServerAPI api,
        IContainerTransactionCapture capture,
        Func<bool> containerAuditEnabled,
        Func<string> worldIdAccessor)
    {
        _api = api;
        _capture = capture;
        _containerAuditEnabled = containerAuditEnabled;
        _worldIdAccessor = worldIdAccessor;
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

            if (_api.World.BlockAccessor.GetBlockEntity(blockSel.Position) is not IBlockEntityContainer container ||
                container.Inventory is not InventoryBase inventory)
            {
                return;
            }

            EnsureInventoryTracked(inventory);
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
        var inventoryId = inventory.InventoryID ?? inventory.ClassName ?? Guid.NewGuid().ToString("N");
        if (!_trackedInventories.TryAdd(inventoryId, 1))
        {
            return;
        }

        inventory.OnInventoryOpened += player => OnInventoryOpened(inventory, player);
        inventory.OnInventoryClosed += player => OnInventoryClosed(inventory, player);
        inventory.SlotModified += slotId => OnSlotModified(inventory, slotId);
    }

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

            var key = new ContainerSessionKey(inventory.InventoryID, player.PlayerUID);
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
            var activeViewerCount = CountSessionsForInventory(inventory.InventoryID);
            foreach (var entry in _sessions.Where(pair => string.Equals(pair.Key.InventoryId, inventory.InventoryID, StringComparison.OrdinalIgnoreCase)))
            {
                var state = entry.Value with
                {
                    WasAmbiguous = entry.Value.WasAmbiguous || activeViewerCount > 1
                };
                _sessions[entry.Key] = state;
            }
        }
        catch (Exception exception)
        {
            _api.Logger.Error("[WorldAudit] Container slot modification capture failed: {0}", exception);
        }
    }

    private void EnsureSessionForPlayer(InventoryBase inventory, IPlayer player, BlockPos? position)
    {
        var inventoryId = inventory.InventoryID;
        if (string.IsNullOrWhiteSpace(inventoryId) || player.PlayerUID is null)
        {
            return;
        }

        var key = new ContainerSessionKey(inventoryId, player.PlayerUID);
        var snapshots = CaptureSlots(inventory);
        var state = new ContainerSessionState(
            Inventory: inventory,
            WorldId: _worldIdAccessor(),
            Position: position ?? inventory.Pos?.Copy(),
            InventoryType: inventory.ClassName ?? "unknown",
            ContainerLabel: ResolveContainerLabel(position ?? inventory.Pos),
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

        var ambiguous = state.WasAmbiguous || CountSessionsForInventory(state.Inventory.InventoryID) > 0;
        var actorName = ambiguous ? "#unknown" : state.ActorName;
        var actorExternalId = ambiguous ? null : state.ActorExternalId;
        var cause = ambiguous ? AuditCause.Unknown : AuditCause.Player;

        _capture.Capture(new ContainerTransactionCaptureContext(
            WorldId: state.WorldId,
            Position: new BlockPosition(position.X, position.Y, position.Z),
            OccurredAt: DateTimeOffset.UtcNow,
            ActorName: actorName,
            ActorExternalId: actorExternalId,
            Cause: cause,
            InventoryType: state.InventoryType,
            ContainerLabel: state.ContainerLabel,
            Lines: lines,
            BeforeSnapshot: state.BeforeSnapshot,
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
                StackBytes: stack?.ToBytes()));
        }

        return snapshots;
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
        string WorldId,
        BlockPos? Position,
        string InventoryType,
        string? ContainerLabel,
        string ActorName,
        string? ActorExternalId,
        IReadOnlyList<SlotSnapshot> BeforeSlots,
        byte[] BeforeSnapshot,
        bool WasAmbiguous);

    private sealed record SlotSnapshot(int SlotId, string? ItemCode, int Quantity, byte[]? StackBytes);
}
