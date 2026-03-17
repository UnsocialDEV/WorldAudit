using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using WorldAudit.Application.Abstractions;
using WorldAudit.Domain;

namespace WorldAudit.Integration.VintageStory;

public sealed class VintageStoryBlockMutationObserver
{
    private static readonly BlockPos[] NeighborOffsets =
    [
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 1, 0),
        new(0, -1, 0),
        new(0, 0, 1),
        new(0, 0, -1)
    ];

    private readonly IBlockMutationSink _capture;
    private readonly BlockMutationScopeManager _scopeManager;
    private readonly Func<bool> _blockAuditEnabled;
    private readonly Func<bool> _fireCauseProviderEnabled;
    private readonly Func<string> _worldIdAccessor;
    private readonly Func<IWorldAccessor> _worldAccessor;
    private readonly VintageStoryBlockEntitySnapshotCodec _snapshotCodec;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string, Exception>? _logFailure;
    private readonly ConcurrentDictionary<PendingRemovalKey, PendingRemoval> _pendingRemovals = new();
    private readonly TimeSpan _pendingRemovalAge = TimeSpan.FromMilliseconds(25);

    public VintageStoryBlockMutationObserver(
        IBlockMutationSink capture,
        BlockMutationScopeManager scopeManager,
        Func<bool> blockAuditEnabled,
        Func<bool>? fireCauseProviderEnabled,
        Func<string> worldIdAccessor,
        Func<IWorldAccessor> worldAccessor,
        VintageStoryBlockEntitySnapshotCodec snapshotCodec,
        Func<DateTimeOffset>? clock = null,
        Action<string, Exception>? logFailure = null)
    {
        _capture = capture;
        _scopeManager = scopeManager;
        _blockAuditEnabled = blockAuditEnabled;
        _fireCauseProviderEnabled = fireCauseProviderEnabled ?? (() => true);
        _worldIdAccessor = worldIdAccessor;
        _worldAccessor = worldAccessor;
        _snapshotCodec = snapshotCodec;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logFailure = logFailure;
    }

    public void OnBlockRemoved(Block block, IWorldAccessor world, BlockPos pos)
    {
        try
        {
            if (!_blockAuditEnabled())
            {
                return;
            }

            var now = _clock();
            var worldId = _worldIdAccessor();
            var position = ToPosition(pos);
            _scopeManager.PruneExpiredPositionScopes(now);
            _scopeManager.TryTakePositionScope(worldId, position, out var positionScope);

            var effectiveScope = positionScope ?? _scopeManager.GetAmbientScope();
            var key = new PendingRemovalKey(worldId, position);
            _pendingRemovals[key] = new PendingRemoval(
                WorldId: worldId,
                Position: position,
                OccurredAt: now,
                OldBlockCode: block.Code?.ToString() ?? "game:air",
                OldBlockEntitySnapshot: effectiveScope?.OldBlockEntitySnapshot ?? _snapshotCodec.Capture(pos),
                Scope: effectiveScope);
        }
        catch (Exception exception)
        {
            _logFailure?.Invoke("block removal capture", exception);
        }
    }

    public void OnBlockPlaced(Block block, IWorldAccessor world, BlockPos pos)
    {
        try
        {
            if (!_blockAuditEnabled())
            {
                return;
            }

            var worldId = _worldIdAccessor();
            var position = ToPosition(pos);
            var key = new PendingRemovalKey(worldId, position);
            var now = _clock();
            var newBlockCode = world.BlockAccessor.GetBlock(pos)?.Code?.ToString() ?? block.Code?.ToString() ?? "game:air";
            var newSnapshot = _snapshotCodec.Capture(pos);
            var ambientScope = _scopeManager.GetAmbientScope();

            if (_pendingRemovals.TryRemove(key, out var pending))
            {
                var observation = new BlockMutationObservation(
                    WorldId: worldId,
                    Position: position,
                    OccurredAt: now,
                    Action: ResolveAction(pending.OldBlockCode, newBlockCode),
                    OldBlockCode: pending.OldBlockCode,
                    NewBlockCode: newBlockCode,
                    OldBlockEntitySnapshot: pending.OldBlockEntitySnapshot,
                    NewBlockEntitySnapshot: newSnapshot,
                    PositionScope: pending.Scope,
                    AmbientScope: ambientScope,
                    CurrentBlockCode: newBlockCode,
                    HasFireNeighbor: HasFireNeighbor(world, pos));

                _capture.Observe(observation);
                return;
            }

            _scopeManager.TryTakePositionScope(worldId, position, out var positionScope);
            var placementObservation = new BlockMutationObservation(
                WorldId: worldId,
                Position: position,
                OccurredAt: now,
                Action: BlockAuditAction.Place,
                OldBlockCode: "game:air",
                NewBlockCode: newBlockCode,
                NewBlockEntitySnapshot: newSnapshot,
                PositionScope: positionScope,
                AmbientScope: ambientScope,
                CurrentBlockCode: newBlockCode,
                HasFireNeighbor: HasFireNeighbor(world, pos));

            _capture.Observe(placementObservation);
        }
        catch (Exception exception)
        {
            _logFailure?.Invoke("block placement capture", exception);
        }
    }

    public void FlushPendingRemovals()
    {
        try
        {
            if (!_blockAuditEnabled())
            {
                _pendingRemovals.Clear();
                return;
            }

            var now = _clock();
            var world = _worldAccessor();

            foreach (var entry in _pendingRemovals)
            {
                if (now - entry.Value.OccurredAt < _pendingRemovalAge)
                {
                    continue;
                }

                if (!_pendingRemovals.TryRemove(entry.Key, out var pending))
                {
                    continue;
                }

                var blockPos = ToBlockPos(pending.Position);
                var currentBlockCode = world.BlockAccessor.GetBlock(blockPos)?.Code?.ToString() ?? "game:air";
                var observation = new BlockMutationObservation(
                    WorldId: pending.WorldId,
                    Position: pending.Position,
                    OccurredAt: pending.OccurredAt,
                    Action: BlockAuditAction.Break,
                    OldBlockCode: pending.OldBlockCode,
                    NewBlockCode: "game:air",
                    OldBlockEntitySnapshot: pending.OldBlockEntitySnapshot,
                    PositionScope: pending.Scope,
                    AmbientScope: _scopeManager.GetAmbientScope(),
                    CurrentBlockCode: currentBlockCode,
                    HasFireNeighbor: HasFireNeighbor(world, blockPos));

                _capture.Observe(observation);
            }
        }
        catch (Exception exception)
        {
            _logFailure?.Invoke("pending block removal flush", exception);
        }
    }

    private static BlockAuditAction ResolveAction(string? oldBlockCode, string? newBlockCode)
    {
        var oldIsAir = string.IsNullOrWhiteSpace(oldBlockCode) || oldBlockCode.EndsWith(":air", StringComparison.OrdinalIgnoreCase);
        var newIsAir = string.IsNullOrWhiteSpace(newBlockCode) || newBlockCode.EndsWith(":air", StringComparison.OrdinalIgnoreCase);

        if (oldIsAir && !newIsAir)
        {
            return BlockAuditAction.Place;
        }

        if (!oldIsAir && newIsAir)
        {
            return BlockAuditAction.Break;
        }

        return BlockAuditAction.Replace;
    }

    private bool HasFireNeighbor(IWorldAccessor world, BlockPos pos)
    {
        if (!_fireCauseProviderEnabled())
        {
            return false;
        }

        foreach (var offset in NeighborOffsets)
        {
            var neighborPos = new BlockPos(pos.X + offset.X, pos.Y + offset.Y, pos.Z + offset.Z);
            var neighborCode = world.BlockAccessor.GetBlock(neighborPos)?.Code?.ToString();
            if (!string.IsNullOrWhiteSpace(neighborCode)
                && neighborCode.Contains("fire", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static BlockPosition ToPosition(BlockPos pos)
    {
        return new BlockPosition(pos.X, pos.Y, pos.Z);
    }

    private static BlockPos ToBlockPos(BlockPosition position)
    {
        return new BlockPos(position.X, position.Y, position.Z);
    }

    private sealed record PendingRemovalKey(string WorldId, BlockPosition Position);

    private sealed record PendingRemoval(
        string WorldId,
        BlockPosition Position,
        DateTimeOffset OccurredAt,
        string OldBlockCode,
        byte[]? OldBlockEntitySnapshot,
        BlockMutationScope? Scope);
}
