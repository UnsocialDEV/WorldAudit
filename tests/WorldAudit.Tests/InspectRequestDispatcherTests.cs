using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Integration.VintageStory;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

public sealed class InspectRequestDispatcherTests
{
    [Fact]
    public async Task RepeatedAttemptsAndSlowCompletionRequireAQuietPeriodBeforeAdvancing()
    {
        var now = DateTimeOffset.UtcNow;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var completionObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new VintageStoryInspectRequestDispatcher(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await finished.Task;
        }, clock: () =>
        {
            if (finished.Task.IsCompleted) completionObserved.TrySetResult();
            return now;
        });
        var player = ProxyFactory.Create<IServerPlayer>(proxy => proxy.SetValue(nameof(IServerPlayer.PlayerUID), "uid"));
        var pos = new BlockPos(1, 65, 1);
        Assert.True(dispatcher.TryDispatch(player, pos));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        now += TimeSpan.FromSeconds(2);
        Assert.False(dispatcher.TryDispatch(player, pos.UpCopy()));
        Assert.False(dispatcher.TryDispatch(player, pos));
        finished.SetResult();
        await completionObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var repeat = 0; repeat < 5; repeat++)
        {
            now += TimeSpan.FromMilliseconds(400);
            Assert.False(dispatcher.TryDispatch(player, pos));
        }
        Assert.Equal(1, calls);
        now += TimeSpan.FromMilliseconds(501);
        Assert.True(dispatcher.TryDispatch(player, pos));
    }
}
