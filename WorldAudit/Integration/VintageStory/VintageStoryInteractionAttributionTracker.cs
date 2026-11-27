using Vintagestory.API.MathTools;

namespace WorldAudit.Integration.VintageStory;

public sealed class VintageStoryInteractionAttributionTracker
{
    private readonly Dictionary<InteractionKey, Queue<InteractionEntry>> _entries = [];
    private readonly object _gate = new();
    private Timer? _expiryTimer;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _ambiguityWindow;

    public VintageStoryInteractionAttributionTracker(
        Func<DateTimeOffset>? clock = null,
        TimeSpan? ttl = null,
        TimeSpan? ambiguityWindow = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _ttl = ttl ?? TimeSpan.FromSeconds(2);
        _ambiguityWindow = ambiguityWindow ?? TimeSpan.FromMilliseconds(250);
    }

    public void Record(string worldId, BlockPos position, string actorName, string? actorExternalId, byte[]? oldBlockEntitySnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(worldId);
        ArgumentNullException.ThrowIfNull(position);

        if (string.IsNullOrWhiteSpace(actorName))
        {
            return;
        }

        var key = new InteractionKey(worldId, position.X, position.InternalY, position.Z);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var queue)) _entries[key] = queue = new();
            var entry = new InteractionEntry(actorName, actorExternalId, oldBlockEntitySnapshot, _clock());
            queue.Enqueue(entry);
            Trim(queue, entry.RecordedAt);
            // Keep weak ownership: a caller may abandon its tracker without disposing it.
            var interval = TimeSpan.FromMilliseconds(Math.Clamp(_ttl.TotalMilliseconds, 1, int.MaxValue));
            _expiryTimer ??= new Timer(static state =>
            {
                if (((WeakReference<VintageStoryInteractionAttributionTracker>)state!).TryGetTarget(out var tracker))
                    tracker.ReleaseExpiredLocations();
            }, new WeakReference<VintageStoryInteractionAttributionTracker>(this), interval, interval);
        }
    }

    public bool TryResolve(string worldId, BlockPos? position, out VintageStoryInteractionResolution? resolution)
    {
        resolution = null;
        if (string.IsNullOrWhiteSpace(worldId) || position is null)
        {
            return false;
        }

        lock (_gate)
        {
            var now = _clock();
            var key = new InteractionKey(worldId, position.X, position.InternalY, position.Z);
            if (!_entries.TryGetValue(key, out var queue))
            {
                return false;
            }

            Trim(queue, now);
            var candidates = queue
                .Where(entry => now - entry.RecordedAt <= _ttl)
                .OrderByDescending(entry => entry.RecordedAt)
                .ToArray();

            if (candidates.Length == 0)
            {
                _entries.Remove(key);
                return false;
            }

            var latest = candidates[0];
            var conflictingCandidate = candidates.FirstOrDefault(entry =>
                !string.Equals(entry.ActorExternalId, latest.ActorExternalId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.ActorName, latest.ActorName, StringComparison.OrdinalIgnoreCase)
                && latest.RecordedAt - entry.RecordedAt <= _ambiguityWindow);

            resolution = new VintageStoryInteractionResolution(
                latest.ActorName,
                latest.ActorExternalId,
                latest.OldBlockEntitySnapshot,
                conflictingCandidate is not null);
            return true;
        }
    }

    private void ReleaseExpiredLocations()
    {
        lock (_gate)
        {
            var now = _clock();
            foreach (var (key, queue) in _entries)
            {
                Trim(queue, now);
                if (queue.Count == 0) _entries.Remove(key);
            }
            if (_entries.Count == 0)
            {
                _expiryTimer?.Dispose();
                _expiryTimer = null;
            }
        }
    }

    private void Trim(Queue<InteractionEntry> queue, DateTimeOffset now)
    {
        while (queue.TryPeek(out var entry) && now - entry.RecordedAt > _ttl)
        {
            queue.Dequeue();
        }
    }

    private sealed record InteractionKey(string WorldId, int X, int Y, int Z);

    private sealed record InteractionEntry(
        string ActorName,
        string? ActorExternalId,
        byte[]? OldBlockEntitySnapshot,
        DateTimeOffset RecordedAt);
}
