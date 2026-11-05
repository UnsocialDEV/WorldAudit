using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Mod;

namespace WorldAudit.Presentation.Commands;

internal sealed class WorldAuditAdminCommandHandler
{
    private readonly WorldAuditCommandContext _context;
    private int _readInProgress;

    public WorldAuditAdminCommandHandler(WorldAuditCommandContext context)
    {
        _context = context;
    }

    public TextCommandResult HandleStatus(TextCommandCallingArgs args)
    {
        return _context.ExecuteSafely("status", () =>
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
                    var runtime = _context.Runtime;
                    return StartRead(args, "purge preview", async () =>
                    {
                        var preview = await runtime.PreviewPurgeAsync(request.Age).ConfigureAwait(false);
                        return WorldAuditCommandText.FormatPurgePreview(preview, request.Age).ToArray();
                    });
                }

                // shortcut: confirmed purge stays synchronous until maintenance is coordinated with rollback jobs.
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

    private TextCommandResult StartRead(TextCommandCallingArgs args, string operation, Func<Task<string[]>> read)
    {
        if (Interlocked.CompareExchange(ref _readInProgress, 1, 0) != 0)
        {
            return TextCommandResult.Error("[WA] A status or purge preview query is already running. Wait for its result.", "query_running");
        }

        var player = args.Caller?.Player as IServerPlayer;
        _ = Task.Run(async () =>
        {
            try
            {
                await WorldAuditSafeExecution.RunAsync(_context.Api, operation,
                    async () => Reply(await read().ConfigureAwait(false), EnumChatType.CommandSuccess),
                    message =>
                    {
                        Reply([message], EnumChatType.CommandError);
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _readInProgress, 0);
            }
        });
        return TextCommandResult.Deferred;

        void Reply(string[] lines, EnumChatType type)
        {
            _context.Api.Event.EnqueueMainThreadTask(() => WorldAuditSafeExecution.Run(_context.Api, $"{operation} reply", () =>
            {
                foreach (var line in lines)
                {
                    if (player is not null)
                    {
                        player.SendMessage(0, line, type, null);
                    }
                    else
                    {
                        _context.Api.Logger.Notification("{0}", line);
                    }
                }
            }), "worldaudit-admin-read");
        }
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
