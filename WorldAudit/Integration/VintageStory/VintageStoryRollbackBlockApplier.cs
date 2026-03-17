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

        var pos = new BlockPos(entry.Position.X, entry.Position.Y, entry.Position.Z);
        var blockAccessor = _api.World.BlockAccessor;
        if (blockAccessor.GetChunkAtBlockPos(pos) is null)
        {
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Conflict, false, null, "Target chunk is not loaded."));
        }

        var currentBlock = blockAccessor.GetBlock(pos);
        var previousBlockCode = currentBlock.Code?.ToString() ?? "game:air";
        var previousBlockEntitySnapshot = _snapshotCodec.Capture(pos);
        var targetCode = string.IsNullOrWhiteSpace(entry.TargetBlockCode) ? "game:air" : entry.TargetBlockCode;
        var targetBlock = _api.World.GetBlock(new AssetLocation(targetCode));
        if (targetBlock is null)
        {
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Failed, false, previousBlockCode, $"Target block '{targetCode}' was not found."));
        }

        if (entry.TargetBlockEntitySnapshot is not null && string.IsNullOrWhiteSpace(targetBlock.EntityClass))
        {
            return Task.FromResult(new RollbackBlockApplyResult(
                RollbackBlockApplyStatus.Conflict,
                false,
                previousBlockCode,
                $"Block '{targetCode}' does not expose a block entity for snapshot restore."));
        }

        var applySucceeded = TryApplyTargetState(pos, targetBlock, targetCode, previousBlockCode, entry.TargetBlockEntitySnapshot, out var applyStatus, out var applyError, out var mutationAttempted);
        if (applySucceeded)
        {
            var changedWorld = HasStateChangedFrom(pos, previousBlockCode, previousBlockEntitySnapshot);
            return Task.FromResult(new RollbackBlockApplyResult(RollbackBlockApplyStatus.Applied, changedWorld, previousBlockCode));
        }

        if (!mutationAttempted)
        {
            return Task.FromResult(new RollbackBlockApplyResult(applyStatus, false, previousBlockCode, applyError));
        }

        var reverted = TryRestorePreviousState(pos, previousBlockCode, previousBlockEntitySnapshot, out var revertError)
            && !HasStateChangedFrom(pos, previousBlockCode, previousBlockEntitySnapshot);
        if (!reverted)
        {
            var error = $"{applyError} Revert failed: {revertError ?? "Block state still differs from its pre-rollback value."}";
            _api.Logger.Error("[WorldAudit] Failed to revert block rollback mutation at {0}: {1}", pos, error);
            return Task.FromResult(new RollbackBlockApplyResult(
                RollbackBlockApplyStatus.Failed,
                HasStateChangedFrom(pos, previousBlockCode, previousBlockEntitySnapshot),
                previousBlockCode,
                error));
        }

        return Task.FromResult(new RollbackBlockApplyResult(applyStatus, false, previousBlockCode, applyError));
    }

    private bool TryApplyTargetState(
        BlockPos pos,
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
                blockAccessor.SetBlock(targetBlock.Id, pos);
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
            else if (!_snapshotCodec.TryValidate(pos, out error))
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

    private bool TryRestorePreviousState(BlockPos pos, string previousBlockCode, byte[]? previousBlockEntitySnapshot, out string? error)
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
            var currentBlockCode = blockAccessor.GetBlock(pos)?.Code?.ToString() ?? "game:air";
            if (!string.Equals(currentBlockCode, previousBlockCode, StringComparison.OrdinalIgnoreCase))
            {
                blockAccessor.SetBlock(previousBlock.Id, pos);
            }

            if (previousBlockEntitySnapshot is null)
            {
                return _snapshotCodec.TryValidate(pos, out error);
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

    private bool HasStateChangedFrom(BlockPos pos, string originalBlockCode, byte[]? originalSnapshot)
    {
        var currentBlockCode = _api.World.BlockAccessor.GetBlock(pos)?.Code?.ToString() ?? "game:air";
        if (!string.Equals(currentBlockCode, originalBlockCode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var currentSnapshot = _snapshotCodec.Capture(pos);
        return !SnapshotsEqual(currentSnapshot, originalSnapshot);
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
}
