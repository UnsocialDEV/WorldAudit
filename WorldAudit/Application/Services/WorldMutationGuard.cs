using System.Collections.Concurrent;
using WorldAudit.Application.Abstractions;
using WorldAudit.Domain;

namespace WorldAudit.Application.Services;

public sealed class WorldMutationGuard : IWorldMutationGuard
{
    private readonly ConcurrentDictionary<MutationKey, int> _activeMutations = [];

    public IDisposable Begin(string worldId, BlockPosition position)
    {
        var key = new MutationKey(worldId, position);
        _activeMutations.AddOrUpdate(key, 1, static (_, count) => count + 1);
        return new Scope(this, key);
    }

    public bool IsActive(string worldId, BlockPosition position)
    {
        return _activeMutations.ContainsKey(new MutationKey(worldId, position));
    }

    private void End(MutationKey key)
    {
        while (true)
        {
            if (!_activeMutations.TryGetValue(key, out var current))
            {
                return;
            }

            if (current <= 1)
            {
                if (_activeMutations.TryRemove(key, out _))
                {
                    return;
                }

                continue;
            }

            if (_activeMutations.TryUpdate(key, current - 1, current))
            {
                return;
            }
        }
    }

    private readonly record struct MutationKey(string WorldId, BlockPosition Position);

    private sealed class Scope : IDisposable
    {
        private readonly WorldMutationGuard _owner;
        private readonly MutationKey _key;
        private int _disposed;

        public Scope(WorldMutationGuard owner, MutationKey key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.End(_key);
            }
        }
    }
}
