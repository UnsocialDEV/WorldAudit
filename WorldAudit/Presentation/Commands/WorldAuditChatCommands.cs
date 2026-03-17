using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Services;
using WorldAudit.Mod;

namespace WorldAudit.Presentation.Commands;

public sealed class WorldAuditChatCommands
{
    private readonly WorldAuditInspectCommandHandler _inspectHandler;
    private readonly WorldAuditLookupCommandHandler _lookupHandler;
    private readonly WorldAuditRollbackCommandHandler _rollbackHandler;
    private readonly WorldAuditAdminCommandHandler _adminHandler;

    public WorldAuditChatCommands(
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
        var context = new WorldAuditCommandContext(
            api,
            runtimeAccessor,
            configAccessor,
            lookupCommandParser,
            purgeCommandParser,
            inspectorState,
            formatterAccessor,
            worldIdAccessor,
            reloadHandler);

        _inspectHandler = new WorldAuditInspectCommandHandler(context);
        _lookupHandler = new WorldAuditLookupCommandHandler(context);
        _rollbackHandler = new WorldAuditRollbackCommandHandler(context);
        _adminHandler = new WorldAuditAdminCommandHandler(context);
        Api = api;
    }

    private ICoreServerAPI Api { get; }

    public void Register()
    {
        var root = Api.ChatCommands
            .Create("worldaudit")
            .WithRootAlias("wa")
            .WithDescription("Block and container audit history commands.")
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(_ => TextCommandResult.Success(WorldAuditCommandText.HelpText, null));

        root.BeginSubCommand("help")
            .WithDescription("Show WorldAudit help.")
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(_ => TextCommandResult.Success(WorldAuditCommandText.HelpText, null))
            .EndSubCommand();

        root.BeginSubCommand("inspect")
            .WithAlias(["i"])
            .WithDescription("Toggle inspect mode for block and container history.")
            .RequiresPlayer()
            .RequiresPrivilege(WorldAuditPrivileges.Inspect)
            .HandleWith(_inspectHandler.HandleInspect)
            .EndSubCommand();

        root.BeginSubCommand("lookup")
            .WithAlias(["l"])
            .WithDescription("Lookup audit history around your current position.")
            .RequiresPlayer()
            .RequiresPrivilege(WorldAuditPrivileges.Lookup)
            .WithArgs([Api.ChatCommands.Parsers.OptionalAll("query")])
            .HandleWith(_lookupHandler.HandleLookup)
            .EndSubCommand();

        root.BeginSubCommand("near")
            .WithDescription("Lookup nearby audit history using the default near radius.")
            .RequiresPlayer()
            .RequiresPrivilege(WorldAuditPrivileges.Lookup)
            .WithArgs([Api.ChatCommands.Parsers.OptionalAll("query")])
            .HandleWith(_lookupHandler.HandleNear)
            .EndSubCommand();

        root.BeginSubCommand("rollback")
            .WithAlias(["rb"])
            .WithDescription("Create a rollback preview job from nearby block history.")
            .RequiresPlayer()
            .RequiresPrivilege(WorldAuditPrivileges.Rollback)
            .WithArgs([Api.ChatCommands.Parsers.OptionalAll("query")])
            .HandleWith(_rollbackHandler.HandleRollback)
            .EndSubCommand();

        root.BeginSubCommand("restore")
            .WithAlias(["rs"])
            .WithDescription("Create a restore preview job from nearby block history.")
            .RequiresPlayer()
            .RequiresPrivilege(WorldAuditPrivileges.Restore)
            .WithArgs([Api.ChatCommands.Parsers.OptionalAll("query")])
            .HandleWith(_rollbackHandler.HandleRestore)
            .EndSubCommand();

        root.BeginSubCommand("undo")
            .WithDescription("Create a preview job that inverts a completed rollback or restore job.")
            .RequiresPlayer()
            .RequiresPrivilege(WorldAuditPrivileges.Restore)
            .WithArgs([Api.ChatCommands.Parsers.OptionalAll("jobId")])
            .HandleWith(_rollbackHandler.HandleUndo)
            .EndSubCommand();

        root.BeginSubCommand("apply")
            .WithDescription("Queue a rollback preview job for execution.")
            .RequiresPlayer()
            .RequiresPrivilege(WorldAuditPrivileges.Rollback)
            .WithArgs([Api.ChatCommands.Parsers.OptionalAll("jobId")])
            .HandleWith(_rollbackHandler.HandleApply)
            .EndSubCommand();

        root.BeginSubCommand("jobs")
            .WithDescription("Show recent rollback jobs.")
            .RequiresPlayer()
            .RequiresPrivilege(WorldAuditPrivileges.Rollback)
            .HandleWith(_rollbackHandler.HandleJobs)
            .EndSubCommand();

        root.BeginSubCommand("job")
            .WithDescription("Show one rollback job.")
            .RequiresPlayer()
            .RequiresPrivilege(WorldAuditPrivileges.Rollback)
            .WithArgs([Api.ChatCommands.Parsers.OptionalAll("jobId")])
            .HandleWith(_rollbackHandler.HandleJob)
            .EndSubCommand();

        root.BeginSubCommand("status")
            .WithDescription("Show WorldAudit database and queue status.")
            .RequiresPrivilege(WorldAuditPrivileges.Status)
            .HandleWith(_adminHandler.HandleStatus)
            .EndSubCommand();

        root.BeginSubCommand("reload")
            .WithDescription("Reload the WorldAudit config.")
            .RequiresPrivilege(WorldAuditPrivileges.Reload)
            .HandleWith(_adminHandler.HandleReload)
            .EndSubCommand();

        root.BeginSubCommand("consumer")
            .WithDescription("Pause or resume WorldAudit DB consumption.")
            .RequiresPrivilege(WorldAuditPrivileges.Consumer)
            .WithArgs([Api.ChatCommands.Parsers.OptionalAll("action")])
            .HandleWith(_adminHandler.HandleConsumer)
            .EndSubCommand();

        root.BeginSubCommand("purge")
            .WithDescription("Preview or confirm an age-based audit purge.")
            .RequiresPrivilege(WorldAuditPrivileges.Purge)
            .WithArgs([Api.ChatCommands.Parsers.OptionalAll("query")])
            .HandleWith(_adminHandler.HandlePurge)
            .EndSubCommand();
    }

    public Task SendInspectHistoryAsync(IServerPlayer player, BlockPos pos) => _inspectHandler.SendInspectHistoryAsync(player, pos);

    public void ShowInspectModeIndicator(IServerPlayer player) => _inspectHandler.ShowInspectModeIndicator(player);

    public void ClearInspectModeIndicator(IServerPlayer player) => _inspectHandler.ClearInspectModeIndicator(player);
}
