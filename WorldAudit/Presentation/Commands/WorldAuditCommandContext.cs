using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Application;
using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Services;
using WorldAudit.Mod;

namespace WorldAudit.Presentation.Commands;

internal sealed class WorldAuditCommandContext
{
    private readonly Func<WorldAuditRuntime> _runtimeAccessor;
    private readonly Func<WorldAuditModConfig> _configAccessor;
    private readonly Func<IResultFormatter> _formatterAccessor;
    private readonly Func<string> _worldIdAccessor;
    private readonly Func<(bool Success, string Message)> _reloadHandler;

    public WorldAuditCommandContext(
        ICoreServerAPI api,
        Func<WorldAuditRuntime> runtimeAccessor,
        Func<WorldAuditModConfig> configAccessor,
        LookupCommandParser lookupCommandParser,
        PurgeCommandParser purgeCommandParser,
        InspectorStateService inspectorState,
        Func<IResultFormatter> formatterAccessor,
        Func<string> worldIdAccessor,
        Func<(bool Success, string Message)> reloadHandler)
    {
        Api = api;
        _runtimeAccessor = runtimeAccessor;
        _configAccessor = configAccessor;
        LookupCommandParser = lookupCommandParser;
        PurgeCommandParser = purgeCommandParser;
        InspectorState = inspectorState;
        _formatterAccessor = formatterAccessor;
        _worldIdAccessor = worldIdAccessor;
        _reloadHandler = reloadHandler;
    }

    public ICoreServerAPI Api { get; }

    public LookupCommandParser LookupCommandParser { get; }

    public PurgeCommandParser PurgeCommandParser { get; }

    public InspectorStateService InspectorState { get; }

    public WorldAuditRuntime Runtime => _runtimeAccessor();

    public WorldAuditModConfig Config => _configAccessor();

    public IResultFormatter Formatter => _formatterAccessor();

    public string GetWorldId() => _worldIdAccessor();

    public (bool Success, string Message) Reload() => _reloadHandler();

    public TextCommandResult ExecuteSafely(string operation, Func<TextCommandResult> action)
    {
        return WorldAuditSafeExecution.Run(
            Api,
            operation,
            action,
            message => TextCommandResult.Error(message, "worldaudit_failure"));
    }
}
