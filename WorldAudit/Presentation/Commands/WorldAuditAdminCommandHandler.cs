using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace WorldAudit.Presentation.Commands;

internal sealed class WorldAuditAdminCommandHandler
{
    private readonly WorldAuditCommandContext _context;

    public WorldAuditAdminCommandHandler(WorldAuditCommandContext context)
    {
        _context = context;
    }

    public TextCommandResult HandleStatus(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely("status", () =>
        {
            var status = _context.Runtime.GetStatusAsync().GetAwaiter().GetResult();
            var lines = WorldAuditCommandText.FormatStatus(status).ToArray();
            SendOptionalLines(args, lines);
            return TextCommandResult.Success(lines[^1], null);
        });
    }

    public TextCommandResult HandleReload(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely("reload", () =>
        {
            var result = _context.Reload();
            return result.Success
                ? TextCommandResult.Success(result.Message, null)
                : TextCommandResult.Error(result.Message, "reload_failed");
        });
    }

    public TextCommandResult HandleConsumer(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely("consumer control", () =>
        {
            var action = args.ArgCount == 0
                ? null
                : args[0] as string ?? args.LastArg as string;

            if (string.Equals(action, "pause", StringComparison.OrdinalIgnoreCase))
            {
                _context.Runtime.PauseConsumer();
                return TextCommandResult.Success("[WA] Consumer paused. Events will queue until resumed or flushed.", null);
            }

            if (string.Equals(action, "resume", StringComparison.OrdinalIgnoreCase))
            {
                _context.Runtime.ResumeConsumer();
                return TextCommandResult.Success("[WA] Consumer resumed.", null);
            }

            return TextCommandResult.Error("[WA] Use /wa consumer pause or /wa consumer resume.", "bad_consumer");
        });
    }

    public TextCommandResult HandlePurge(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely("purge", () =>
        {
            try
            {
                var request = _context.PurgeCommandParser.Parse(WorldAuditCommandText.GetQueryTokens(args).ToArray());
                if (!request.Confirm)
                {
                    var preview = _context.Runtime.PreviewPurgeAsync(request.Age).GetAwaiter().GetResult();
                    var lines = WorldAuditCommandText.FormatPurgePreview(preview, request.Age).ToArray();
                    SendOptionalLines(args, lines);
                    return TextCommandResult.Success(lines[^1], null);
                }

                var result = _context.Runtime.ExecutePurgeAsync(request.Age).GetAwaiter().GetResult();
                var resultLines = WorldAuditCommandText.FormatPurgeResult(result).ToArray();
                SendOptionalLines(args, resultLines);
                return TextCommandResult.Success(resultLines[^1], null);
            }
            catch (FormatException exception)
            {
                return TextCommandResult.Error($"[WA] {exception.Message}", "bad_purge");
            }
            catch (InvalidOperationException exception)
            {
                return TextCommandResult.Error($"[WA] {exception.Message}", "purge_blocked");
            }
        });
    }

    private static void SendOptionalLines(TextCommandCallingArgs args, IReadOnlyList<string> lines)
    {
        if (args.Caller?.Player is not IServerPlayer player)
        {
            return;
        }

        foreach (var line in lines)
        {
            player.SendMessage(0, line, EnumChatType.CommandSuccess, null);
        }
    }
}
