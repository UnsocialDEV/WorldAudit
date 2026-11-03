using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Application.Abstractions;
using WorldAudit.Domain;

namespace WorldAudit.Integration.VintageStory;

public sealed class VintageStoryRollbackBlockApplier : IRollbackBlockApplier
{
    private readonly ICoreServerAPI _api;
    private readonly VintageStoryBlockEntitySnapshotCodec _snapshotCodec;

    public VintageStoryRollbackBlockApplier(ICoreServerAPI api)
    {
        _api = api;
        _snapshotCodec = new VintageStoryBlockEntitySnapshotCodec(api.World);
    }

    public Task<RollbackBlockApplyResult> ApplyAsync(RollbackBlockPlanEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var currentWorldId = _api.WorldManager?.SaveGame?.SavegameIdentifier;
        if (!string.IsNullOrWhiteSpace(currentWorldId) && !string.Equals(currentWorldId, entry.WorldId, StringComparison.Ordinal))
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Conflict, false, null, "This entry belongs to another world."));

        var pos = new BlockPos(entry.Position.X, entry.Position.Y, entry.Position.Z);
        var blockAccessor = _api.World.BlockAccessor;
        if (blockAccessor.GetChunkAtBlockPos(pos) is null)
        {
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Conflict, false, null, "Target chunk is not loaded."));
        }

        if (entry.Layer is not (1 or 2))
        {
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Conflict, false, null, "The recorded block layer is unknown."));
        }

        var currentBlock = blockAccessor.GetBlock(pos, entry.Layer);
        var previousBlockCode = currentBlock.Code?.ToString() ?? "game:air";
        byte[]? previousBlockEntitySnapshot;
        try
        {
            previousBlockEntitySnapshot = entry.Layer == 1 ? _snapshotCodec.Capture(pos) : null;
        }
        catch (Exception exception)
        {
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Conflict, false, previousBlockCode, $"Cannot preserve the current block entity: {exception.Message}"));
        }

        var expectedCode = string.IsNullOrWhiteSpace(entry.ExpectedBlockCode) ? "game:air" : entry.ExpectedBlockCode;
        if (!string.Equals(previousBlockCode, expectedCode, StringComparison.OrdinalIgnoreCase)
            || !SnapshotsEqual(previousBlockEntitySnapshot, entry.ExpectedBlockEntitySnapshot)
            || (entry.Layer == 1 && !string.IsNullOrWhiteSpace(currentBlock.EntityClass) && previousBlockEntitySnapshot is null))
        {
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Conflict, false, previousBlockCode,
                "Current block state no longer matches the recorded source state."));
        }
        var targetCode = string.IsNullOrWhiteSpace(entry.TargetBlockCode) ? "game:air" : entry.TargetBlockCode;
        var targetBlock = _api.World.GetBlock(new AssetLocation(targetCode));
        if (targetBlock is null)
        {
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Failed, false, previousBlockCode, $"Target block '{targetCode}' was not found."));
        }

        if (targetBlock.Id != 0 && targetBlock.ForFluidsLayer != (entry.Layer == BlockLayersAccess.Fluid))
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Conflict, false, previousBlockCode,
                "Target block does not belong in the recorded layer."));

        if (entry.Layer == BlockLayersAccess.Solid && targetBlock.Id != 0 && !string.Equals(previousBlockCode, targetCode, StringComparison.OrdinalIgnoreCase)
            && targetBlock.DisplacesLiquids(blockAccessor, pos)
            && blockAccessor.GetBlock(pos, BlockLayersAccess.Fluid) is { Id: not 0 })
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Conflict, false, previousBlockCode,
                "Solid replacement would destroy current fluid; resolve the fluid history first."));

        if (entry.Layer == 1 && !string.IsNullOrWhiteSpace(targetBlock.EntityClass) && entry.TargetBlockEntitySnapshot is null)
        {
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Conflict, false, previousBlockCode,
                "The target block entity snapshot is missing; refusing to restore incomplete state."));
        }

        if (entry.TargetBlockEntitySnapshot is not null && string.IsNullOrWhiteSpace(targetBlock.EntityClass))
        {
            return Task.FromResult(new RollbackBlockApplyResult(
                RollbackBlockApplyStatus.Conflict,
                false,
                previousBlockCode,
                $"Block '{targetCode}' does not expose a block entity for snapshot restore."));
        }

        var applySucceeded = TryApplyTargetState(pos, entry.Layer, targetBlock, targetCode, previousBlockCode, entry.TargetBlockEntitySnapshot, out var applyStatus, out var applyError, out var mutationAttempted);
        if (applySucceeded)
        {
            try
            {
                var actualCode = blockAccessor.GetBlock(pos, entry.Layer)?.Code?.ToString() ?? "game:air";
                var actualSnapshot = entry.Layer == 1 ? _snapshotCodec.Capture(pos) : null;
                if (!string.Equals(actualCode, targetCode, StringComparison.OrdinalIgnoreCase)
                    || (entry.TargetBlockEntitySnapshot is not null && actualSnapshot is null))
                    throw new InvalidOperationException("The resulting block state could not be verified.");
                var changedWorld = !string.Equals(actualCode, previousBlockCode, StringComparison.OrdinalIgnoreCase)
                    || !SnapshotsEqual(actualSnapshot, previousBlockEntitySnapshot);
                return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Applied, changedWorld, previousBlockCode,
                    PreviousBlockEntitySnapshot: previousBlockEntitySnapshot, ActualBlockCode: actualCode, ActualBlockEntitySnapshot: actualSnapshot));
            }
            catch (Exception exception)
            {
                applyStatus = RollbackBlockApplyStatus.Failed;
                applyError = $"Failed to verify the applied block state: {exception.Message}";
            }
        }

        if (!mutationAttempted)
        {
            return Task.FromResult(new RollbackBlockApplyResult(applyStatus, false, previousBlockCode, applyError));
        }

        var reverted = TryRestorePreviousState(pos, entry.Layer, previousBlockCode, previousBlockEntitySnapshot, out var revertError);
        try
        {
            reverted = reverted && !HasStateChangedFrom(pos, entry.Layer, previousBlockCode, previousBlockEntitySnapshot);
        }
        catch (Exception exception)
        {
            reverted = false;
            revertError = $"Cannot verify reverted state: {exception.Message}";
        }
        if (!reverted)
        {
            var error = $"{applyError} Revert failed: {revertError ?? "Block state still differs from its pre-rollback value."}";
            _api.Logger.Error("[WorldAudit] Failed to revert block rollback mutation at {0}: {1}", pos, error);
            byte[]? actualSnapshot = null;
            try { actualSnapshot = entry.Layer == 1 ? _snapshotCodec.Capture(pos) : null; }
            catch (Exception exception) { error += $" Final snapshot unavailable: {exception.Message}"; }
            return Task.FromResult(new RollbackBlockApplyResult(
                RollbackBlockApplyStatus.Failed,
                true,
                previousBlockCode,
                error,
                previousBlockEntitySnapshot,
                blockAccessor.GetBlock(pos, entry.Layer)?.Code?.ToString() ?? "game:air",
                actualSnapshot));
        }

        return Task.FromResult(new RollbackBlockApplyResult(applyStatus, false, previousBlockCode, applyError));
    }

    private bool TryApplyTargetState(
        BlockPos pos,
        int layer,
        Block targetBlock,
        string targetCode,
        string previousBlockCode,
        byte[]? targetBlockEntitySnapshot,
        out RollbackBlockApplyStatus status,
        out string? error,
        out bool mutationAttempted)
    {
        mutationAttempted = false;
        try
        {
            var blockAccessor = _api.World.BlockAccessor;
            if (!string.Equals(previousBlockCode, targetCode, StringComparison.OrdinalIgnoreCase))
            {
                mutationAttempted = true;
                ReplaceBlock(pos, layer, targetBlock);
            }

            if (targetBlockEntitySnapshot is not null)
            {
                mutationAttempted = true;
                if (!_snapshotCodec.TryRestore(pos, targetBlockEntitySnapshot, out error))
                {
                    status = RollbackBlockApplyStatus.Conflict;
                    return false;
                }
            }
            else if (layer == 1 && !_snapshotCodec.TryValidate(pos, out error))
            {
                status = RollbackBlockApplyStatus.Conflict;
                return false;
            }

            status = RollbackBlockApplyStatus.Applied;
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            status = RollbackBlockApplyStatus.Failed;
            error = $"Failed to apply target block state: {exception.Message}";
            return false;
        }
    }

    private bool TryRestorePreviousState(BlockPos pos, int layer, string previousBlockCode, byte[]? previousBlockEntitySnapshot, out string? error)
    {
        error = null;
        try
        {
            var previousBlock = _api.World.GetBlock(new AssetLocation(previousBlockCode));
            if (previousBlock is null)
            {
                error = $"Previous block '{previousBlockCode}' was not found for revert.";
                return false;
            }

            var blockAccessor = _api.World.BlockAccessor;
            var currentBlockCode = blockAccessor.GetBlock(pos, layer)?.Code?.ToString() ?? "game:air";
            if (!string.Equals(currentBlockCode, previousBlockCode, StringComparison.OrdinalIgnoreCase))
            {
                ReplaceBlock(pos, layer, previousBlock);
            }

            if (previousBlockEntitySnapshot is null)
            {
                return layer != 1 || _snapshotCodec.TryValidate(pos, out error);
            }

            if (string.IsNullOrWhiteSpace(previousBlock.EntityClass))
            {
                error = $"Previous block '{previousBlockCode}' does not expose a block entity for revert.";
                return false;
            }

            return _snapshotCodec.TryRestore(pos, previousBlockEntitySnapshot, out error);
        }
        catch (Exception exception)
        {
            error = $"Failed to revert block state: {exception.Message}";
            return false;
        }
    }

    private bool HasStateChangedFrom(BlockPos pos, int layer, string originalBlockCode, byte[]? originalSnapshot)
    {
        var currentBlockCode = _api.World.BlockAccessor.GetBlock(pos, layer)?.Code?.ToString() ?? "game:air";
        if (!string.Equals(currentBlockCode, originalBlockCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var currentSnapshot = layer == 1 ? _snapshotCodec.Capture(pos) : null;
        return !SnapshotsEqual(currentSnapshot, originalSnapshot);
    }

    private void ReplaceBlock(BlockPos pos, int layer, Block block)
    {
        var accessor = _api.World.BlockAccessor;
        if (layer == BlockLayersAccess.Fluid)
        {
            accessor.SetBlock(block.Id, pos, layer);
            return;
        }
        // Replay each recorded cell without gameplay removal callbacks deleting unplanned multiblock parts.
        accessor.RemoveBlockEntity(pos);
        accessor.ExchangeBlock(block.Id, pos);
        if (!string.IsNullOrWhiteSpace(block.EntityClass)) accessor.SpawnBlockEntity(block.EntityClass, pos);
        accessor.MarkBlockDirty(pos);
    }

    private bool SnapshotsEqual(byte[]? left, byte[]? right) => _snapshotCodec.AreEquivalent(left, right);
}
