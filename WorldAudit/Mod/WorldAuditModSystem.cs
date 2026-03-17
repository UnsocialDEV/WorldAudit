using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Application.Services;
using WorldAudit.Integration.VintageStory;
using WorldAudit.Mod.Telemetry;
using WorldAudit.Presentation.Commands;

namespace WorldAudit.Mod;

public sealed class WorldAuditModSystem : ModSystem
{
    private const string MutationBehaviorCode = "worldaudit:mutationobserver";
    private const string ConfigFileName = "worldaudit.json";
    private static readonly TimeSpan InspectIndicatorRefreshInterval = TimeSpan.FromMilliseconds(500);

    private ICoreServerAPI? _api;
    private WorldAuditRuntime? _runtime;
    private WorldAuditRuntimeBinder? _runtimeBinder;
    private WorldAuditRuntimeReloader? _runtimeReloader;
    private InspectorStateService? _inspectorState;
    private VintageStoryBlockEventBridge? _eventBridge;
    private VintageStoryContainerEventBridge? _containerEventBridge;
    private WorldAuditChatCommands? _chatCommands;
    private WorldAuditModConfig? _config;
    private WorldAuditTelemetryService? _telemetry;
    private long _rollbackTickListenerId;
    private DateTimeOffset _nextInspectIndicatorRefreshAtUtc = DateTimeOffset.MinValue;

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Server;
    }

    public override void StartPre(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Server)
        {
            return;
        }

        api.RegisterBlockBehaviorClass(MutationBehaviorCode, typeof(WorldAuditBlockMutationBehavior));
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        if (api.Side != EnumAppSide.Server)
        {
            return;
        }

        AttachMutationBehavior(api);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        _api = api;
        _config = WorldAuditConfigLoader.LoadOrCreate(api, ConfigFileName);
        _runtime = CreateRuntime(api, _config);
        _inspectorState = new InspectorStateService();
        _runtimeBinder = new WorldAuditRuntimeBinder(
            api,
            () => _config,
            () => _inspectorState,
            () => _chatCommands,
            GetWorldId,
            ReportFailure);
        _runtimeReloader = new WorldAuditRuntimeReloader(
            api,
            config => CreateRuntime(api, config),
            runtime => CreateBlockEventBridge(api, runtime),
            runtime => CreateContainerEventBridge(api, runtime),
                interval => RegisterRollbackTickListener(api, interval));
        _telemetry = new WorldAuditTelemetryService(api, () => _runtime, () => _config, GetWorldId);
        _telemetry.RegisterGlobalHandlers();

        RegisterPrivileges(api);

        _chatCommands = new WorldAuditChatCommands(
            api,
            () => _runtime ?? throw new InvalidOperationException("WorldAudit runtime is not initialized."),
            () => _config ?? throw new InvalidOperationException("WorldAudit config is not initialized."),
            new LookupCommandParser(),
            new PurgeCommandParser(),
            _inspectorState,
            () => (_runtime ?? throw new InvalidOperationException("WorldAudit runtime is not initialized.")).ResultFormatter,
            GetWorldId,
            ReloadRuntime);
        _chatCommands.Register();

        BindRuntime(api, _runtime, _config);
        RunLifecycleOperation(
            "startup maintenance",
            () =>
            {
                var status = _runtime.ProcessMaintenanceTickAsync(force: true).GetAwaiter().GetResult();
                if (!status.Succeeded)
                {
                    _telemetry?.ReportMaintenanceFailure(status);
                }
            });

        api.Event.GameWorldSave += OnGameWorldSave;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        _telemetry.ReportStartup();
        api.Logger.Notification("[WorldAudit] Performance hardening features registered.");
    }

    public override void Dispose()
    {
        var api = _api;
        WorldAuditSafeExecution.Run(api, "mod disposal", () =>
        {
            if (api is not null)
            {
                api.Event.GameWorldSave -= OnGameWorldSave;
                api.Event.PlayerDisconnect -= OnPlayerDisconnect;
                if (_rollbackTickListenerId != 0)
                {
                    api.Event.UnregisterGameTickListener(_rollbackTickListenerId);
                    _rollbackTickListenerId = 0;
                }
            }

            _eventBridge?.Unregister();
            WorldAuditBlockMutationBehaviorRuntime.Reset();
            _containerEventBridge?.Unregister();
            _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _telemetry?.Dispose();
        });

        _runtime = null;
        _runtimeBinder = null;
        _runtimeReloader = null;
        _eventBridge = null;
        _containerEventBridge = null;
        _chatCommands = null;
        _inspectorState = null;
        _telemetry = null;
        _api = null;
        _config = null;
    }

    private void OnGameWorldSave()
    {
        RunLifecycleOperation("game world save", () =>
        {
            _eventBridge?.FlushPendingMutations();
            _runtime?.FlushAsync().GetAwaiter().GetResult();
        });
    }

    private void OnPlayerDisconnect(IServerPlayer byPlayer)
    {
        RunLifecycleOperation("player disconnect cleanup", () =>
        {
            _chatCommands?.ClearInspectModeIndicator(byPlayer);
            _inspectorState?.Remove(byPlayer.PlayerUID);
        });
    }

    private string GetWorldId()
    {
        return _api?.World.WorldName ?? "main";
    }

    private static void RegisterPrivileges(ICoreServerAPI api)
    {
        api.Permissions.RegisterPrivilege(WorldAuditPrivileges.Inspect, "Use WorldAudit inspect mode.", true);
        api.Permissions.RegisterPrivilege(WorldAuditPrivileges.Lookup, "Use WorldAudit lookup commands.", true);
        api.Permissions.RegisterPrivilege(WorldAuditPrivileges.LookupBlock, "Use WorldAudit block lookup commands.", true);
        api.Permissions.RegisterPrivilege(WorldAuditPrivileges.LookupContainer, "Use WorldAudit container lookup commands.", true);
        api.Permissions.RegisterPrivilege(WorldAuditPrivileges.Rollback, "Preview and apply WorldAudit rollback jobs.", true);
        api.Permissions.RegisterPrivilege(WorldAuditPrivileges.Restore, "Preview and apply WorldAudit restore jobs.", true);
        api.Permissions.RegisterPrivilege(WorldAuditPrivileges.Purge, "Preview and execute WorldAudit purge operations.", true);
        api.Permissions.RegisterPrivilege(WorldAuditPrivileges.Reload, "Reload the WorldAudit config and runtime.", true);
        api.Permissions.RegisterPrivilege(WorldAuditPrivileges.Status, "View WorldAudit status output.", true);
        api.Permissions.RegisterPrivilege(WorldAuditPrivileges.Consumer, "Pause and resume WorldAudit DB consumption.", true);
        api.Permissions.RegisterPrivilege(WorldAuditPrivileges.Admin, "Use elevated WorldAudit admin operations.", true);
    }

    private static void AttachMutationBehavior(ICoreAPI api)
    {
        foreach (var block in api.World.Blocks)
        {
            if (block is null)
            {
                continue;
            }

            var existing = block.BlockBehaviors ?? Array.Empty<BlockBehavior>();
            if (existing.Any(behavior => behavior is WorldAuditBlockMutationBehavior))
            {
                continue;
            }

            var updated = new BlockBehavior[existing.Length + 1];
            Array.Copy(existing, updated, existing.Length);
            updated[^1] = new WorldAuditBlockMutationBehavior(block);
            block.BlockBehaviors = updated;
        }
    }

    private void BindRuntime(ICoreServerAPI api, WorldAuditRuntime runtime, WorldAuditModConfig config)
    {
        _eventBridge = CreateBlockEventBridge(api, runtime);
        _eventBridge.Register();

        _containerEventBridge = CreateContainerEventBridge(api, runtime);
        _containerEventBridge.Register();

        RegisterRollbackTickListener(api, config.RollbackTickIntervalMilliseconds);
    }

    private VintageStoryBlockEventBridge CreateBlockEventBridge(ICoreServerAPI api, WorldAuditRuntime runtime)
    {
        return (_runtimeBinder ?? throw new InvalidOperationException("WorldAudit runtime binder is not initialized."))
            .CreateBlockEventBridge(runtime);
    }

    private VintageStoryContainerEventBridge CreateContainerEventBridge(ICoreServerAPI api, WorldAuditRuntime runtime)
    {
        return (_runtimeBinder ?? throw new InvalidOperationException("WorldAudit runtime binder is not initialized."))
            .CreateContainerEventBridge(runtime);
    }

    private static WorldAuditRuntime CreateRuntime(ICoreServerAPI api, WorldAuditModConfig config)
    {
        var options = config.ToRuntimeOptions(api);
        api.Logger.Notification("[WorldAudit] SQLite path: {0}", options.DatabasePath);
        return WorldAuditRuntime.CreateAsync(
            options,
            new VintageStoryRollbackBlockApplier(api),
            new VintageStoryRollbackContainerApplier(api),
            message => api.Logger.Notification(message)).GetAwaiter().GetResult();
    }

    private long RegisterRollbackTickListener(ICoreServerAPI api, int intervalMilliseconds)
    {
        _rollbackTickListenerId = (_runtimeBinder ?? throw new InvalidOperationException("WorldAudit runtime binder is not initialized."))
            .RegisterRollbackTickListener(
                _rollbackTickListenerId,
                intervalMilliseconds,
                () =>
                {
                    _eventBridge?.FlushPendingMutations();
                    _runtime?.ProcessRollbackTickAsync().GetAwaiter().GetResult();
                    _runtime?.ProcessMaintenanceTickAsync().GetAwaiter().GetResult();
                    RefreshInspectIndicators();
                });
        return _rollbackTickListenerId;
    }

    private void RefreshInspectIndicators()
    {
        if (_api is null || _chatCommands is null || _inspectorState is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextInspectIndicatorRefreshAtUtc)
        {
            return;
        }

        _nextInspectIndicatorRefreshAtUtc = now + InspectIndicatorRefreshInterval;
        var enabledPlayerUids = _inspectorState.GetEnabledPlayerUids();
        if (enabledPlayerUids.Count == 0)
        {
            return;
        }

        foreach (var player in _api.World.AllOnlinePlayers)
        {
            if (player is not IServerPlayer serverPlayer || string.IsNullOrWhiteSpace(serverPlayer.PlayerUID))
            {
                continue;
            }

            if (!_inspectorState.IsEnabled(serverPlayer.PlayerUID))
            {
                continue;
            }

            _chatCommands.ShowInspectModeIndicator(serverPlayer);
        }
    }

    private (bool Success, string Message) ReloadRuntime()
    {
        return WorldAuditSafeExecution.Run(
            _api,
            "reload",
            () =>
            {
                if (_api is null || _runtime is null || _config is null)
                {
                    return (false, "[WA] WorldAudit runtime is not initialized.");
                }

                if (_runtime.HasActiveRollbackJobs)
                {
                    return (false, "[WA] Reload is blocked while rollback jobs are queued or running.");
                }

                if (!WorldAuditConfigLoader.TryLoad(_api, ConfigFileName, out var newConfig, out var error))
                {
                    return (false, $"[WA] {error}");
                }

                try
                {
                    var reloadState = CreateReloadState();
                    var result = (_runtimeReloader ?? throw new InvalidOperationException("WorldAudit runtime reloader is not initialized."))
                        .Reload(reloadState, newConfig);
                    if (result.Success)
                    {
                        ApplyReloadState(result.State);
                        _telemetry?.ReportStartup();
                    }

                    return (result.Success, result.Message);
                }
                catch (Exception exception)
                {
                    return (false, $"[WA] Reload failed: {exception.Message}");
                }
            },
            message => (false, message),
            ReportFailure);
    }

    private void RunLifecycleOperation(string operation, Action action)
    {
        WorldAuditSafeExecution.Run(_api, operation, action, ReportFailure);
    }

    private WorldAuditRuntimeReloadState CreateReloadState()
    {
        return new WorldAuditRuntimeReloadState(
            _runtime ?? throw new InvalidOperationException("WorldAudit runtime is not initialized."),
            _config ?? throw new InvalidOperationException("WorldAudit config is not initialized."),
            _eventBridge ?? throw new InvalidOperationException("WorldAudit block event bridge is not initialized."),
            _containerEventBridge ?? throw new InvalidOperationException("WorldAudit container event bridge is not initialized."),
            _rollbackTickListenerId);
    }

    private void ApplyReloadState(WorldAuditRuntimeReloadState state)
    {
        _runtime = state.Runtime;
        _config = state.Config;
        _eventBridge = state.EventBridge;
        _containerEventBridge = state.ContainerEventBridge;
        _rollbackTickListenerId = state.RollbackTickListenerId;
    }

    private void ReportFailure(string operation, Exception exception)
    {
        _telemetry?.ReportOperationFailure(operation, exception);
    }

}
