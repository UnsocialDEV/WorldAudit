using Vintagestory.API.Common;
using Vintagestory.API.Server;
using WorldAudit.Mod;

namespace WorldAudit.Presentation.Commands;

internal sealed class BackgroundAuditCommand(ICoreServerAPI api, System.Func<IServerPlayer, int> auditGroup)
{
    private int _running;
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public TextCommandResult Run<T>(IServerPlayer? player, string operation, Func<Task<T>> work, Action<T> reply)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return TextCommandResult.Error("[WA] An audit command is already running. Wait for its result.", "query_running");

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work().ConfigureAwait(false);
                Post(() => reply(result));
            }
            catch (Exception exception)
            {
                api.Logger.Error("[WorldAudit] {0} failed: {1}", operation, exception);
                var message = WorldAuditSafeExecution.BuildUserMessage(operation, exception);
                Post(() =>
                {
                    if (player is null) api.Logger.Notification("{0}", message);
                    else player.SendAuditLine(auditGroup(player), message, EnumChatType.CommandError);
                });
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
        return TextCommandResult.Deferred;

        void Post(Action send) => api.Event.EnqueueMainThreadTask(
            () => WorldAuditSafeExecution.Run(api, operation, send), "worldaudit-command-reply");
    }
}
