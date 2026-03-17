using Vintagestory.API.Server;
using WorldAudit.Integration.VintageStory;

namespace WorldAudit.Mod;

internal sealed class WorldAuditRuntimeReloader
{
    private readonly ICoreServerAPI _api;
    private readonly Func<WorldAuditModConfig, WorldAuditRuntime> _runtimeFactory;
    private readonly Func<WorldAuditRuntime, VintageStoryBlockEventBridge> _blockBridgeFactory;
    private readonly Func<WorldAuditRuntime, VintageStoryContainerEventBridge> _containerBridgeFactory;
    private readonly Func<int, long> _tickListenerRegistrar;

    public WorldAuditRuntimeReloader(
        ICoreServerAPI api,
        Func<WorldAuditModConfig, WorldAuditRuntime> runtimeFactory,
        Func<WorldAuditRuntime, VintageStoryBlockEventBridge> blockBridgeFactory,
        Func<WorldAuditRuntime, VintageStoryContainerEventBridge> containerBridgeFactory,
        Func<int, long> tickListenerRegistrar)
    {
        _api = api;
        _runtimeFactory = runtimeFactory;
        _blockBridgeFactory = blockBridgeFactory;
        _containerBridgeFactory = containerBridgeFactory;
        _tickListenerRegistrar = tickListenerRegistrar;
    }

    public (bool Success, string Message, WorldAuditRuntimeReloadState State) Reload(
        WorldAuditRuntimeReloadState currentState,
        WorldAuditModConfig newConfig)
    {
        try
        {
            currentState.EventBridge.FlushPendingMutations();
            currentState.Runtime.FlushAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            return (false, $"[WA] Failed to flush pending audit data before reload: {exception.Message}", currentState);
        }

        var wasPaused = currentState.Runtime.IsConsumerPaused;
        WorldAuditRuntime? newRuntime = null;
        VintageStoryBlockEventBridge? newEventBridge = null;
        VintageStoryContainerEventBridge? newContainerBridge = null;
        var previousStateDetached = false;

        try
        {
            newRuntime = _runtimeFactory(newConfig);
            if (wasPaused)
            {
                newRuntime.PauseConsumer();
            }

            newEventBridge = _blockBridgeFactory(newRuntime);
            newContainerBridge = _containerBridgeFactory(newRuntime);

            currentState.EventBridge.Unregister();
            WorldAuditBlockMutationBehaviorRuntime.Reset();
            currentState.ContainerEventBridge.Unregister();
            if (currentState.RollbackTickListenerId != 0)
            {
                _api.Event.UnregisterGameTickListener(currentState.RollbackTickListenerId);
            }

            previousStateDetached = true;

            newEventBridge.Register();
            newContainerBridge.Register();
            var newTickListenerId = _tickListenerRegistrar(newConfig.RollbackTickIntervalMilliseconds);
            newRuntime.ProcessMaintenanceTickAsync(force: true).GetAwaiter().GetResult();

            currentState.Runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return (true, "[WA] WorldAudit config reloaded.", new WorldAuditRuntimeReloadState(newRuntime, newConfig, newEventBridge, newContainerBridge, newTickListenerId));
        }
        catch (Exception exception)
        {
            try
            {
                newEventBridge?.Unregister();
                newContainerBridge?.Unregister();
                newRuntime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }

            if (previousStateDetached)
            {
                try
                {
                    currentState.EventBridge.Register();
                    currentState.ContainerEventBridge.Register();
                    var restoredTickListenerId = _tickListenerRegistrar(currentState.Config.RollbackTickIntervalMilliseconds);
                    currentState = currentState with { RollbackTickListenerId = restoredTickListenerId };
                }
                catch
                {
                }
            }

            return (false, $"[WA] Reload failed: {exception.Message}", currentState);
        }
    }
}
