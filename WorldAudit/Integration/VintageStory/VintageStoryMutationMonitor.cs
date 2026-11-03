using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using WorldAudit.Application.Abstractions;
using WorldAudit.Domain;

namespace WorldAudit.Integration.VintageStory;

// Snapshots belong to the complete operation: doors, tools and chisels finish initializing after SetBlock.
internal sealed class VintageStoryMutationMonitor(
    IWorldAccessor world, IBlockMutationSink capture, BlockMutationScopeManager scopes,
    Func<string> worldId, Func<bool> enabled, Action<Exception> reportFailure)
{
    private readonly VintageStoryBlockEntitySnapshotCodec _codec = new(world);
    private readonly AsyncLocal<Operation?> _operation = new();

    internal IDisposable Begin(BlockMutationScope? scope = null)
    {
        var lease = scope is null ? null : scopes.BeginAmbientScope(scope);
        var operation = _operation.Value;
        if (operation is null || operation.Depth == 0) _operation.Value = operation = new Operation();
        operation.Depth++;
        return new Lease(this, operation, lease);
    }

    internal BlockMutationScope? CurrentScope => scopes.GetAmbientScope();

    internal void Touch(BlockPos pos, BlockMutationScope? scope = null, IBlockAccessor? accessor = null, int? onlyLayer = null)
    {
        var operation = _operation.Value;
        if (operation is null || !enabled()) return;
        foreach (var layer in new[] { BlockLayersAccess.Solid, BlockLayersAccess.Fluid })
        {
            if (onlyLayer is not null && onlyLayer != layer) continue;
            var key = (new BlockPosition(pos.X, pos.InternalY, pos.Z), layer);
            if (operation.Before.ContainsKey(key)) continue;
            try
            {
                var block = (accessor ?? world.BlockAccessor).GetBlock(pos, layer);
                if (block is null) continue;
                operation.Before.Add(key, new Before(block.Code?.ToString() ?? "game:air",
                    layer == BlockLayersAccess.Solid && accessor is null ? _codec.Capture(pos) : null,
                    scope ?? scopes.GetAmbientScope(), accessor));
            }
            catch (Exception exception) { reportFailure(exception); }
        }
    }

    private void End(Operation operation)
    {
        if (--operation.Depth != 0) return;
        _operation.Value = null;
        foreach (var (key, before) in operation.Before)
        {
            try
            {
                var (position, layer) = key;
                var pos = new BlockPos(position.X, position.Y, position.Z);
                var block = (before.Accessor ?? world.BlockAccessor).GetBlock(pos, layer);
                if (block is null) continue;
                var code = block.Code?.ToString() ?? "game:air";
                if (code == before.Code && world.BlockAccessor.GetBlockEntity(pos) is IBlockEntityContainer) continue;
                var snapshot = layer == BlockLayersAccess.Solid && before.Accessor is null ? _codec.Capture(pos) : null;
                if (code == before.Code && SameBytes(snapshot, before.Snapshot)) continue;
                capture.Observe(new BlockMutationObservation(worldId(), position, DateTimeOffset.UtcNow,
                    code == "game:air" ? BlockAuditAction.Break : before.Code == "game:air" ? BlockAuditAction.Place : BlockAuditAction.Replace,
                    before.Code, code, before.Snapshot, snapshot, AmbientScope: before.Scope, Layer: layer));
            }
            catch (Exception exception) { reportFailure(exception); }
        }
    }

    private static bool SameBytes(byte[]? left, byte[]? right)
        => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private sealed record Before(string Code, byte[]? Snapshot, BlockMutationScope? Scope, IBlockAccessor? Accessor);
    private sealed class Operation
    {
        internal int Depth;
        internal Dictionary<(BlockPosition, int), Before> Before { get; } = new();
    }
    private sealed class Lease(VintageStoryMutationMonitor owner, Operation operation, IDisposable? scope) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { owner.End(operation); }
            finally { scope?.Dispose(); }
        }
    }
}
