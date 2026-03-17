using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Application.Abstractions;
using WorldAudit.Integration;
using WorldAudit.Integration.VintageStory;

namespace WorldAudit.Tests;

public sealed class VintageStoryBlockEventBridgeTests
{
    [Fact]
    public void CanPlaceOrBreakBlock_InspectMode_BlocksInteractionWithoutSendingHistory()
    {
        var inspectCalls = 0;
        var bridge = CreateBridge(
            inspectHandler: (_, _) =>
            {
                inspectCalls++;
                return Task.CompletedTask;
            },
            isInspectEnabled: _ => true);
        var player = CreateServerPlayer("player-1", "Dayton");
        var selection = new BlockSelection { Position = new BlockPos(3, 65, 7) };

        var result = InvokeCanPlaceOrBreakBlock(bridge, player, selection, out var claimant);

        Assert.False(result);
        Assert.Equal("worldauditinspectsilent", claimant);
        Assert.Equal(1, inspectCalls);
    }

    [Fact]
    public void CanPlaceOrBreakBlock_InspectMode_DebouncesRepeatedAttemptsAtSameBlock()
    {
        var inspectCalls = 0;
        var bridge = CreateBridge(
            inspectHandler: (_, _) =>
            {
                inspectCalls++;
                return Task.CompletedTask;
            },
            isInspectEnabled: _ => true);
        var player = CreateServerPlayer("player-1", "Dayton");
        var selection = new BlockSelection { Position = new BlockPos(3, 65, 7) };

        var firstResult = InvokeCanPlaceOrBreakBlock(bridge, player, selection, out _);
        var secondResult = InvokeCanPlaceOrBreakBlock(bridge, player, selection, out _);

        Assert.False(firstResult);
        Assert.False(secondResult);
        Assert.Equal(1, inspectCalls);
    }

    [Fact]
    public void DidUseBlock_InspectMode_SendsHistoryOnce()
    {
        var inspectCalls = 0;
        var bridge = CreateBridge(
            inspectHandler: (_, pos) =>
            {
                inspectCalls++;
                Assert.Equal(new BlockPos(4, 66, 8), pos);
                return Task.CompletedTask;
            },
            isInspectEnabled: _ => true);
        var player = CreateServerPlayer("player-1", "Dayton");
        var selection = new BlockSelection { Position = new BlockPos(4, 66, 8) };

        InvokeDidUseBlock(bridge, player, selection);

        Assert.Equal(1, inspectCalls);
    }

    private static VintageStoryBlockEventBridge CreateBridge(
        System.Func<IServerPlayer, BlockPos, Task> inspectHandler,
        System.Func<string, bool> isInspectEnabled)
    {
        return new VintageStoryBlockEventBridge(
            api: null!,
            capture: new NoOpBlockMutationSink(),
            scopeManager: new BlockMutationScopeManager(),
            inspectHandler: inspectHandler,
            inspectIndicatorHandler: _ => { },
            blockAuditEnabled: () => true,
            fireCauseProviderEnabled: () => true,
            worldIdAccessor: () => "main",
            isInspectEnabled: isInspectEnabled,
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

    private static IServerPlayer CreateServerPlayer(string playerUid, string playerName)
    {
        var proxy = DispatchProxy.Create<IServerPlayer, InterfaceProxy<IServerPlayer>>();
        var playerProxy = (InterfaceProxy<IServerPlayer>)(object)proxy;
        playerProxy.SetValue(nameof(IServerPlayer.PlayerUID), playerUid);
        playerProxy.SetValue(nameof(IServerPlayer.PlayerName), playerName);
        return proxy;
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

    private class InterfaceProxy<T> : DispatchProxy
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public void SetValue(string memberName, object? value)
        {
            _values[memberName] = value;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new ArgumentNullException(nameof(targetMethod));
            }

            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
            {
                var memberName = targetMethod.Name[4..];
                if (_values.TryGetValue(memberName, out var value))
                {
                    return value;
                }
            }

            if (targetMethod.Name == nameof(object.ToString))
            {
                return typeof(T).Name;
            }

            return targetMethod.ReturnType.IsValueType
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
