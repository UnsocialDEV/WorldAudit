using Vintagestory.API.MathTools;
using WorldAudit.Integration.VintageStory;

namespace WorldAudit.Tests;

public sealed class VintageStoryInteractionAttributionTrackerTests
{
    [Fact]
    public async Task ExpiredSnapshotsAtOneOffLocationsAreCollectedWithoutMoreInteractions()
    {
        var tracker = new VintageStoryInteractionAttributionTracker(ttl: TimeSpan.FromMilliseconds(80));
        var snapshots = Enumerable.Range(0, 128).Select(RecordSnapshot).ToArray();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (snapshots.Any(snapshot => snapshot.IsAlive) && timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(40);
            GC.Collect();
        }
        Assert.All(snapshots, snapshot => Assert.False(snapshot.IsAlive));
        GC.KeepAlive(tracker);

        WeakReference RecordSnapshot(int x)
        {
            var snapshot = new byte[1024];
            tracker.Record("save", new BlockPos(x, 65, 4, 0), "Player", "uid", snapshot);
            return new WeakReference(snapshot);
        }
    }

    [Fact]
    public void TryResolve_PrefersLatestStrongCandidate()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var tracker = new VintageStoryInteractionAttributionTracker(() => clock);
        var position = new BlockPos(4, 65, 4);

        tracker.Record("main", position, "First", "player-1");
        clock = clock.AddMilliseconds(500);
        tracker.Record("main", position, "Second", "player-2");

        var resolved = tracker.TryResolve("main", position, out var resolution);

        Assert.True(resolved);
        Assert.NotNull(resolution);
        Assert.False(resolution.IsAmbiguous);
        Assert.Equal("Second", resolution.ActorName);
        Assert.Equal("player-2", resolution.ActorExternalId);
    }

    [Fact]
    public void TryResolve_FlagsConflictingNearSimultaneousInteractions()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var tracker = new VintageStoryInteractionAttributionTracker(() => clock);
        var position = new BlockPos(7, 70, 7);

        tracker.Record("main", position, "First", "player-1");
        clock = clock.AddMilliseconds(100);
        tracker.Record("main", position, "Second", "player-2");

        var resolved = tracker.TryResolve("main", position, out var resolution);

        Assert.True(resolved);
        Assert.NotNull(resolution);
        Assert.True(resolution.IsAmbiguous);
        Assert.Equal("Second", resolution.ActorName);
    }
}
