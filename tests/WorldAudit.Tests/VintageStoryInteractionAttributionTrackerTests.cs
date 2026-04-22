using Vintagestory.API.MathTools;
using WorldAudit.Integration.VintageStory;

namespace WorldAudit.Tests;

public sealed class VintageStoryInteractionAttributionTrackerTests
{
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
