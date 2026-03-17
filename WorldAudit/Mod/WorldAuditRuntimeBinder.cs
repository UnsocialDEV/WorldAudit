using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Application.Services;
using WorldAudit.Integration.VintageStory;
using WorldAudit.Presentation.Commands;

namespace WorldAudit.Mod;

internal sealed class WorldAuditRuntimeBinder
{
    private readonly ICoreServerAPI _api;
    private readonly Func<WorldAuditModConfig?> _configAccessor;
    private readonly Func<InspectorStateService?> _inspectorStateAccessor;
    private readonly Func<WorldAuditChatCommands?> _chatCommandsAccessor;
    private readonly Func<string> _worldIdAccessor;
    private readonly Action<string, Exception>? _failureReporter;

    public WorldAuditRuntimeBinder(
        ICoreServerAPI api,
        Func<WorldAuditModConfig?> configAccessor,
        Func<InspectorStateService?> inspectorStateAccessor,
        Func<WorldAuditChatCommands?> chatCommandsAccessor,
        Func<string> worldIdAccessor,
        Action<string, Exception>? failureReporter = null)
    {
        _api = api;
        _configAccessor = configAccessor;
        _inspectorStateAccessor = inspectorStateAccessor;
        _chatCommandsAccessor = chatCommandsAccessor;
        _worldIdAccessor = worldIdAccessor;
        _failureReporter = failureReporter;
    }

    public VintageStoryBlockEventBridge CreateBlockEventBridge(WorldAuditRuntime runtime)
    {
        return new VintageStoryBlockEventBridge(
            _api,
            runtime.BlockMutationCapture,
            runtime.BlockMutationScopes,
            (player, pos) => (_chatCommandsAccessor() ?? throw new InvalidOperationException("WorldAudit commands are not initialized.")).SendInspectHistoryAsync(player, pos),
            player => (_chatCommandsAccessor() ?? throw new InvalidOperationException("WorldAudit commands are not initialized.")).ShowInspectModeIndicator(player),
            () => _configAccessor()?.EnableBlockAudit ?? true,
            () => _configAccessor()?.EnableFireCauseProvider ?? true,
            _worldIdAccessor,
            uid => _inspectorStateAccessor()?.IsEnabled(uid) ?? false,
            new VintageStoryBlockEntitySnapshotCodec(_api.World));
    }

    public VintageStoryContainerEventBridge CreateContainerEventBridge(WorldAuditRuntime runtime)
    {
        return new VintageStoryContainerEventBridge(
            _api,
            runtime.ContainerCapture,
            () => _configAccessor()?.EnableContainerAudit ?? true,
            _worldIdAccessor);
    }

    public long RegisterRollbackTickListener(long currentListenerId, int intervalMilliseconds, Action tickAction)
    {
        if (currentListenerId != 0)
        {
            _api.Event.UnregisterGameTickListener(currentListenerId);
        }

        return _api.Event.RegisterGameTickListener(
            _ => WorldAuditSafeExecution.Run(_api, "rollback or maintenance tick", tickAction, _failureReporter),
            exception =>
            {
                _api.Logger.Error("[WorldAudit] Rollback or maintenance tick failed: {0}", exception);
                _failureReporter?.Invoke("rollback or maintenance tick", exception);
            },
            intervalMilliseconds);
    }
}
