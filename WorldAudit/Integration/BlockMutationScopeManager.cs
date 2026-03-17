using System.Collections.Concurrent;
using WorldAudit.Domain;

namespace WorldAudit.Integration;

public sealed class BlockMutationScopeManager
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);
    private const int SweepBudget = 64;

    private readonly ConcurrentDictionary<PositionScopeKey, BlockMutationScope> _positionScopes = new();
    private readonly AsyncLocal<AmbientScopeNode?> _ambientScope = new();
    private long _nextSweepTicksUtc;

    public void SeedPositionScope(string worldId, BlockPosition position, BlockMutationScope scope)
    {
        ArgumentNullException.ThrowIfNull(worldId);
        ArgumentNullException.ThrowIfNull(scope);

        SweepExpiredPositionScopesIfDue(DateTimeOffset.UtcNow);
        _positionScopes[new PositionScopeKey(worldId, position)] = scope;
    }

    public bool TryTakePositionScope(string worldId, BlockPosition position, out BlockMutationScope? scope)
    {
        ArgumentNullException.ThrowIfNull(worldId);

        SweepExpiredPositionScopesIfDue(DateTimeOffset.UtcNow);
        var key = new PositionScopeKey(worldId, position);
        if (!_positionScopes.TryRemove(key, out scope))
        {
            return false;
        }

        if (scope.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            scope = null;
            return false;
        }

        return true;
    }

    public BlockMutationScope? GetAmbientScope()
    {
        var node = _ambientScope.Value;
        while (node is not null)
        {
            if (node.Scope.ExpiresAt is not { } expiresAt || expiresAt > DateTimeOffset.UtcNow)
            {
                return node.Scope;
            }

            node = node.Parent;
            _ambientScope.Value = node;
        }

        return null;
    }

    public IDisposable BeginAmbientScope(BlockMutationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        _ambientScope.Value = new AmbientScopeNode(scope, _ambientScope.Value);
        return new AmbientScopeLease(this, _ambientScope.Value);
    }

    public void PruneExpiredPositionScopes(DateTimeOffset now)
    {
        SweepExpiredPositionScopes(now, int.MaxValue);
    }

    private void SweepExpiredPositionScopesIfDue(DateTimeOffset now)
    {
        var nowTicks = now.UtcDateTime.Ticks;
        var nextSweep = Volatile.Read(ref _nextSweepTicksUtc);
        if (nowTicks < nextSweep)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _nextSweepTicksUtc, now.Add(SweepInterval).UtcDateTime.Ticks, nextSweep) != nextSweep)
        {
            return;
        }

        SweepExpiredPositionScopes(now, SweepBudget);
    }

    private void SweepExpiredPositionScopes(DateTimeOffset now, int budget)
    {
        foreach (var entry in _positionScopes)
        {
            if (budget-- <= 0)
            {
                break;
            }

            if (entry.Value.ExpiresAt is { } expiresAt && expiresAt <= now)
            {
                _positionScopes.TryRemove(entry.Key, out _);
            }
        }
    }

    private void EndAmbientScope(AmbientScopeNode? leaseNode)
    {
        if (leaseNode is null)
        {
            return;
        }

        var current = _ambientScope.Value;
        if (ReferenceEquals(current, leaseNode))
        {
            _ambientScope.Value = current?.Parent;
        }
    }

    private sealed class AmbientScopeLease : IDisposable
    {
        private readonly BlockMutationScopeManager _manager;
        private readonly AmbientScopeNode? _node;
        private bool _disposed;

        public AmbientScopeLease(BlockMutationScopeManager manager, AmbientScopeNode? node)
        {
            _manager = manager;
            _node = node;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _manager.EndAmbientScope(_node);
        }
    }

    private sealed record AmbientScopeNode(BlockMutationScope Scope, AmbientScopeNode? Parent);

    private sealed record PositionScopeKey(string WorldId, BlockPosition Position);
}
