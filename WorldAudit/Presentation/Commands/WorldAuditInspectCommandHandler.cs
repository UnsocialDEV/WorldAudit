using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using WorldAudit.Domain;
using WorldAudit.Mod;
using WorldAudit.Presentation.Chat;

namespace WorldAudit.Presentation.Commands;

internal sealed class WorldAuditInspectCommandHandler
{
    private const int InspectPageSize = 3;
    private const int InspectHistoryLimit = 60;
    private const string InspectModeIndicatorCode = "worldaudit-inspect-mode";
    private const string InspectModeIndicatorText = "[WorldAudit] Inspection mode enabled";

    private readonly WorldAuditCommandContext _context;
    private readonly InspectChatTableFormatter _inspectFormatter = new();

    public WorldAuditInspectCommandHandler(WorldAuditCommandContext context)
    {
        _context = context;
    }

    public Task SendInspectHistoryAsync(IServerPlayer player, BlockPos pos)
    {
        return WorldAuditSafeExecution.Run(
            _context.Api,
            "inspect history",
            () =>
            {
                var blockPosition = new BlockPosition(pos.X, pos.Y, pos.Z);
                var worldId = _context.GetWorldId();
                _context.Runtime.FlushAsync().GetAwaiter().GetResult();
                var blockResults = _context.Runtime.BlockQueries
                    .GetHistoryAsync(worldId, blockPosition, limit: InspectHistoryLimit)
                    .GetAwaiter()
                    .GetResult();
                var containerResults = _context.Runtime.ContainerQueries
                    .GetHistoryAsync(worldId, blockPosition, limit: InspectHistoryLimit)
                    .GetAwaiter()
                    .GetResult();

                SendInspectPage(player, pos, worldId, blockPosition, blockResults, containerResults);
                ShowInspectModeIndicator(player);
                return Task.CompletedTask;
            },
            message =>
            {
                player.SendMessage(0, message, EnumChatType.CommandError, null);
                return Task.CompletedTask;
            });
    }

    public void ShowInspectModeIndicator(IServerPlayer player)
    {
        _context.Api.SendIngameError(player, InspectModeIndicatorCode, InspectModeIndicatorText, Array.Empty<object>());
    }

    public void ClearInspectModeIndicator(IServerPlayer player)
    {
        _context.Api.SendIngameError(player, InspectModeIndicatorCode, string.Empty, Array.Empty<object>());
    }

    public TextCommandResult HandleInspect(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely("inspect", () =>
        {
            if (args.Caller?.Player is not IServerPlayer player)
            {
                return TextCommandResult.Error("This command requires a player caller.", "not_player");
            }

            var enabled = _context.InspectorState.Toggle(player.PlayerUID);
            if (enabled)
            {
                ShowInspectModeIndicator(player);
            }
            else
            {
                ClearInspectModeIndicator(player);
            }

            var message = enabled
                ? "[WA] Inspect mode enabled. Use, open, or break a block/container to view its history."
                : "[WA] Inspect mode disabled.";

            return TextCommandResult.Success(message, null);
        });
    }

    private void SendInspectPage(
        IServerPlayer player,
        BlockPos pos,
        string worldId,
        BlockPosition position,
        IReadOnlyList<BlockAuditEvent> blockResults,
        IReadOnlyList<ContainerAuditTransaction> containerResults)
    {
        var rows = WorldAuditCommandText.BuildInspectRows(blockResults, containerResults);
        var page = _context.InspectorState.AdvancePage(player.PlayerUID, worldId, position, rows.Count, InspectPageSize);
        var pageRows = rows
            .Skip(page.Offset)
            .Take(page.PageSize)
            .ToArray();

        var start = rows.Count == 0 ? 0 : page.Offset + 1;
        var end = page.Offset + pageRows.Length;
        var lines = _inspectFormatter.Format(
            WorldAuditCommandText.FormatDisplayPosition(_context.Api, pos),
            page.PageNumber,
            page.TotalPages,
            start,
            end,
            page.TotalEventCount,
            pageRows,
            page.NextPage);

        foreach (var line in lines)
        {
            player.SendMessage(0, line, EnumChatType.CommandSuccess, null);
        }
    }
}
