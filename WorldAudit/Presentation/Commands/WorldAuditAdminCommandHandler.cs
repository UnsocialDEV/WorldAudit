using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Mod;

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
        return _context.ExecuteSafely(args, "status", () =>
        {
            var runtime = _context.Runtime;
            var version = _context.Api.ModLoader.GetMod("worldaudit")?.Info.Version ?? "unknown";
            return StartRead(args, "status", async () =>
            {
                var status = await runtime.GetStatusAsync().ConfigureAwait(false);
                return new[] { $"[WA] WorldAudit {version}" }.Concat(WorldAuditCommandText.FormatStatus(status)).ToArray();
            });
        });
    }

    public TextCommandResult HandleReload(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely(args, "reload", () =>
        {
            if (_context.Background.IsRunning)
                return TextCommandResult.Error("[WA] Wait for the active audit command before reloading.", "query_running");
            var result = _context.Reload();
            return result.Success
                ? TextCommandResult.Success(result.Message, null)
                : TextCommandResult.Error(result.Message, "reload_failed");
        });
    }

    public TextCommandResult HandleConsumer(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely(args, "consumer control", () =>
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
        return _context.ExecuteSafely(args, "purge", () =>
        {
            try
            {
                var request = _context.PurgeCommandParser.Parse(WorldAuditCommandText.GetQueryTokens(args).ToArray());
                if (!request.Confirm)
                {
                    var runtime = _context.Runtime;
                    return StartRead(args, "purge preview", async () =>
                    {
                        var preview = await runtime.PreviewPurgeAsync(request.Age).ConfigureAwait(false);
                        return WorldAuditCommandText.FormatPurgePreview(preview, request.Age).ToArray();
                    });
                }

                var purgeRuntime = _context.Runtime;
                return StartRead(args, "confirmed purge", async () =>
                {
                    var result = await purgeRuntime.ExecutePurgeAsync(request.Age).ConfigureAwait(false);
                    return WorldAuditCommandText.FormatPurgeResult(result).ToArray();
                });
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

    private TextCommandResult StartRead(TextCommandCallingArgs args, string operation, Func<Task<string[]>> read)
    {
        var player = args.Caller?.Player as IServerPlayer;
        return _context.Background.Run(player, operation, read, lines =>
        {
            foreach (var line in lines)
            {
                if (player is null) _context.Api.Logger.Notification("{0}", line);
                else player.SendAuditLine(_context.AuditGroup(player), line, EnumChatType.CommandSuccess);
            }
        });
    }
}
