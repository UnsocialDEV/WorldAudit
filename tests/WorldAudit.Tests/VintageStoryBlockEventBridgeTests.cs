using System.Diagnostics;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Application.Abstractions;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Integration.VintageStory;
using WorldAudit.Tests.TestSupport;

namespace WorldAudit.Tests;

public sealed class VintageStoryBlockEventBridgeTests
{
    [Fact]
    public async Task CanPlaceOrBreakBlock_InspectMode_BlocksInteractionWithoutWaitingForHistory()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspectCalls = 0;
        var bridge = CreateBridge(
            inspectHandler: async (_, _) =>
            {
                inspectCalls++;
                started.TrySetResult();
                await release.Task;
            },
            isInspectEnabled: _ => true);
        var player = CreateServerPlayer("player-1", "Dayton");
        var selection = new BlockSelection { Position = new BlockPos(3, 65, 7) };
        var stopwatch = Stopwatch.StartNew();

        var result = InvokeCanPlaceOrBreakBlock(bridge, player, selection, out var claimant);

        stopwatch.Stop();
        Assert.False(result);
        Assert.Equal("worldauditinspectsilent", claimant);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, inspectCalls);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
        release.TrySetResult();
    }

    [Fact]
    public async Task CanPlaceOrBreakBlock_InspectMode_DebouncesRepeatedAttemptsAtSameBlock()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspectCalls = 0;
        var bridge = CreateBridge(
            inspectHandler: async (_, _) =>
            {
                inspectCalls++;
                started.TrySetResult();
                await release.Task;
            },
            isInspectEnabled: _ => true);
        var player = CreateServerPlayer("player-1", "Dayton");
        var selection = new BlockSelection { Position = new BlockPos(3, 65, 7) };

        var firstResult = InvokeCanPlaceOrBreakBlock(bridge, player, selection, out _);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondResult = InvokeCanPlaceOrBreakBlock(bridge, player, selection, out _);

        Assert.False(firstResult);
        Assert.False(secondResult);
        Assert.Equal(1, inspectCalls);
        release.TrySetResult();
    }

    [Fact]
    public async Task CanPlaceOrBreakBlock_InspectMode_AllowsRetryAfterCooldownWhenPreviousDispatchFinished()
    {
        var inspectCalls = 0;
        var bridge = CreateBridge(
            inspectHandler: async (_, _) =>
            {
                Interlocked.Increment(ref inspectCalls);
                await Task.Yield();
            },
            isInspectEnabled: _ => true);
        var player = CreateServerPlayer("player-1", "Dayton");
        var selection = new BlockSelection { Position = new BlockPos(3, 65, 7) };

        var firstResult = InvokeCanPlaceOrBreakBlock(bridge, player, selection, out _);
        await Task.Delay(350);
        var secondResult = InvokeCanPlaceOrBreakBlock(bridge, player, selection, out _);
        await Task.Delay(100);

        Assert.False(firstResult);
        Assert.False(secondResult);
        Assert.Equal(2, inspectCalls);
    }

    [Fact]
    public async Task DidUseBlock_InspectMode_DispatchesHistoryOnceWithoutBlocking()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspectCalls = 0;
        var bridge = CreateBridge(
            inspectHandler: async (_, pos) =>
            {
                inspectCalls++;
                Assert.Equal(new BlockPos(4, 66, 8), pos);
                started.TrySetResult();
                await release.Task;
            },
            isInspectEnabled: _ => true);
        var player = CreateServerPlayer("player-1", "Dayton");
        var selection = new BlockSelection { Position = new BlockPos(4, 66, 8) };
        var stopwatch = Stopwatch.StartNew();

        InvokeDidUseBlock(bridge, player, selection);

        stopwatch.Stop();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, inspectCalls);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
        release.TrySetResult();
    }

    [Fact]
    public async Task DidUseBlock_InspectMode_DeduplicatesWhileRequestIsInFlight()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspectCalls = 0;
        var bridge = CreateBridge(
            inspectHandler: async (_, _) =>
            {
                inspectCalls++;
                started.TrySetResult();
                await release.Task;
            },
            isInspectEnabled: _ => true);
        var player = CreateServerPlayer("player-1", "Dayton");
        var selection = new BlockSelection { Position = new BlockPos(4, 66, 8) };

        InvokeDidUseBlock(bridge, player, selection);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        InvokeDidUseBlock(bridge, player, selection);

        Assert.Equal(1, inspectCalls);
        release.TrySetResult();
    }

    [Fact]
    public async Task BlockInteractStart_InspectMode_OnContainer_ConsumesInteractionAndDispatchesOnce()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspectCalls = 0;
        var selection = new BlockSelection { Position = new BlockPos(5, 65, 5) };
        var bridge = CreateBridge(
            inspectHandler: async (_, pos) =>
            {
                inspectCalls++;
                Assert.Equal(selection.Position, pos);
                started.TrySetResult();
                await release.Task;
            },
            isInspectEnabled: _ => true,
            blockEntityAtSelection: new TestBlockEntityContainer(CreateInventory()));
        var player = CreateServerPlayer("player-1", "Dayton");

        var handled = InvokeObservedBlockInteractStart(bridge, player, selection);

        Assert.True(handled);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, inspectCalls);
        release.TrySetResult();
    }

    [Fact]
    public async Task DidUseBlock_InspectMode_OnContainer_DoesNotDispatchAgainAfterPreUseInspect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspectCalls = 0;
        var selection = new BlockSelection { Position = new BlockPos(5, 65, 5) };
        var bridge = CreateBridge(
            inspectHandler: async (_, _) =>
            {
                inspectCalls++;
                started.TrySetResult();
                await release.Task;
            },
            isInspectEnabled: _ => true,
            blockEntityAtSelection: new TestBlockEntityContainer(CreateInventory()));
        var player = CreateServerPlayer("player-1", "Dayton");

        Assert.True(InvokeObservedBlockInteractStart(bridge, player, selection));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        InvokeDidUseBlock(bridge, player, selection);

        Assert.Equal(1, inspectCalls);
        release.TrySetResult();
    }

    [Fact]
    public void BlockInteractStart_SeedsPlayerScopeForSubsequentMutations()
    {
        var world = CreateWorldAccessor();
        var bridge = new VintageStoryBlockEventBridge(
            api: null!,
            capture: new NoOpBlockMutationSink(),
            scopeManager: new BlockMutationScopeManager(),
            inspectHandler: (_, _) => Task.CompletedTask,
            inspectIndicatorHandler: _ => { },
            blockAuditEnabled: () => true,
            fireCauseProviderEnabled: () => true,
            worldIdAccessor: () => "main",
            isInspectEnabled: _ => false,
            interactionTracker: new VintageStoryInteractionAttributionTracker(),
            snapshotCodec: new VintageStoryBlockEntitySnapshotCodec(world));
        var player = CreateServerPlayer("player-1", "Dayton");
        var selection = new BlockSelection { Position = new BlockPos(8, 70, 8) };

        InvokeObservedBlockInteractStart(bridge, player, selection);

        var scopeManagerField = typeof(VintageStoryBlockEventBridge).GetField("_scopeManager", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Scope manager field was not found.");
        var scopeManager = Assert.IsType<BlockMutationScopeManager>(scopeManagerField.GetValue(bridge));
        var taken = scopeManager.TryTakePositionScope("main", new BlockPosition(8, 70, 8), out var scope);

        Assert.True(taken);
        Assert.NotNull(scope);
        Assert.Equal("Dayton", scope.ActorName);
        Assert.Equal("player-1", scope.ActorExternalId);
        Assert.Equal(AuditCause.Player, scope.Cause);
    }

    private static VintageStoryBlockEventBridge CreateBridge(
        System.Func<IServerPlayer, BlockPos, Task> inspectHandler,
        System.Func<string, bool> isInspectEnabled,
        BlockEntity? blockEntityAtSelection = null)
    {
        return new VintageStoryBlockEventBridge(
            api: CreateServerApi(blockEntityAtSelection),
            capture: new NoOpBlockMutationSink(),
            scopeManager: new BlockMutationScopeManager(),
            inspectHandler: inspectHandler,
            inspectIndicatorHandler: _ => { },
            blockAuditEnabled: () => true,
            fireCauseProviderEnabled: () => true,
            worldIdAccessor: () => "main",
            isInspectEnabled: isInspectEnabled,
            interactionTracker: new VintageStoryInteractionAttributionTracker(),
            snapshotCodec: null!);
    }

    private static bool InvokeCanPlaceOrBreakBlock(
        VintageStoryBlockEventBridge bridge,
        IServerPlayer player,
        BlockSelection selection,
        out string claimant)
    {
        var method = typeof(VintageStoryBlockEventBridge).GetMethod("OnCanPlaceOrBreakBlock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OnCanPlaceOrBreakBlock was not found.");
        var arguments = new object?[] { player, selection, null };
        var result = (bool)(method.Invoke(bridge, arguments) ?? false);
        claimant = arguments[2] as string ?? string.Empty;
        return result;
    }

    private static void InvokeDidUseBlock(
        VintageStoryBlockEventBridge bridge,
        IServerPlayer player,
        BlockSelection selection)
    {
        var method = typeof(VintageStoryBlockEventBridge).GetMethod("OnDidUseBlock", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OnDidUseBlock was not found.");
        method.Invoke(bridge, [player, selection]);
    }

    private static bool InvokeObservedBlockInteractStart(
        VintageStoryBlockEventBridge bridge,
        IPlayer player,
        BlockSelection selection)
    {
        var method = typeof(VintageStoryBlockEventBridge).GetMethod("OnObservedBlockInteractStart", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OnObservedBlockInteractStart was not found.");
        return (bool)(method.Invoke(bridge, [player, selection]) ?? false);
    }

    private static IServerPlayer CreateServerPlayer(string playerUid, string playerName)
    {
        return ProxyFactory.Create<IServerPlayer>(proxy =>
        {
            proxy.SetValue(nameof(IServerPlayer.PlayerUID), playerUid);
            proxy.SetValue(nameof(IServerPlayer.PlayerName), playerName);
        });
    }

    private static IServerWorldAccessor CreateWorldAccessor(BlockEntity? blockEntity = null)
    {
        var blockAccessor = ProxyFactory.Create<IBlockAccessor>(proxy =>
        {
            proxy.SetHandler("GetBlockEntity", _ => blockEntity!);
            proxy.SetHandler("GetBlock", _ => new Block());
        });

        return ProxyFactory.Create<IServerWorldAccessor>(proxy =>
        {
            proxy.SetValue(nameof(IWorldAccessor.BlockAccessor), blockAccessor);
        });
    }

    private static ICoreServerAPI CreateServerApi(BlockEntity? blockEntity = null)
    {
        var logger = ProxyFactory.Create<ILogger>(proxy => { });
        var world = CreateWorldAccessor(blockEntity);
        return ProxyFactory.Create<ICoreServerAPI>(proxy =>
        {
            proxy.SetValue(nameof(ICoreServerAPI.Logger), logger);
            proxy.SetValue(nameof(ICoreServerAPI.World), world);
        });
    }

    private static IInventory CreateInventory()
    {
        return ProxyFactory.Create<IInventory>(proxy => { });
    }

    private sealed class TestBlockEntityContainer : BlockEntity, IBlockEntityContainer
    {
        public TestBlockEntityContainer(IInventory inventory)
        {
            Inventory = inventory;
        }

        public IInventory Inventory { get; }

        public string InventoryClassName => "inventory-generic";

        public void DropContents(Vec3d atPos)
        {
        }

        public void CheckInventoryClearedMidTick()
        {
        }
    }

    private sealed class NoOpBlockMutationSink : IBlockMutationSink
    {
        public void Observe(BlockMutationObservation observation)
        {
        }

        public ValueTask ObserveAsync(BlockMutationObservation observation, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }
}
