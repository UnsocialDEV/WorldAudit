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
    private static readonly TimeSpan InspectFlushTimeout = TimeSpan.FromMilliseconds(50);
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
        return WorldAuditSafeExecution.RunAsync(
            _context.Api,
            "inspect history",
            async () =>
            {
                var blockPosition = new BlockPosition(pos.X, pos.Y, pos.Z);
                var worldId = _context.GetWorldId();
                await TryFlushRecentAuditAsync().ConfigureAwait(false);
                var blockResults = await _context.Runtime.BlockQueries
                    .GetHistoryAsync(worldId, blockPosition, limit: InspectHistoryLimit)
                    .ConfigureAwait(false);
                var containerResults = await _context.Runtime.ContainerQueries
                    .GetHistoryAsync(worldId, blockPosition, limit: InspectHistoryLimit)
                    .ConfigureAwait(false);

                await EnqueueMainThreadAsync(
                    () =>
                    {
                        SendInspectPage(player, pos, worldId, blockPosition, blockResults, containerResults);
                        ShowInspectModeIndicator(player);
                    },
                    "worldaudit-inspect-history").ConfigureAwait(false);
            },
            message =>
            {
                return EnqueueMainThreadAsync(
                    () => player.SendMessage(0, message, EnumChatType.CommandError, null),
                    "worldaudit-inspect-history-error");
            });
    }

    private async Task TryFlushRecentAuditAsync()
    {
        var flushTask = _context.Runtime.FlushAsync();
        var completed = await Task.WhenAny(flushTask, Task.Delay(InspectFlushTimeout)).ConfigureAwait(false);
        if (completed == flushTask)
        {
            await flushTask.ConfigureAwait(false);
        }
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

    private Task EnqueueMainThreadAsync(Action action, string code)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (_context.Api.Event is null)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Api.Event.EnqueueMainThreadTask(
            () =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            code);

        return completion.Task;
    }
}
