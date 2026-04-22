using Vintagestory.API.Server;

namespace WorldAudit.Mod;

internal static class WorldAuditSafeExecution
{
    public static async Task RunAsync(
        ICoreServerAPI? api,
        string operation,
        Func<Task> action,
        Func<string, Task>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log(api, operation, exception);
            if (onFailure is not null)
            {
                await onFailure(BuildUserMessage(operation, exception)).ConfigureAwait(false);
            }
        }
    }

    public static void Run(ICoreServerAPI? api, string operation, Action action, Action<string, Exception>? onFailure = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            action();
        }
        catch (Exception exception)
        {
            Log(api, operation, exception);
            onFailure?.Invoke(operation, exception);
        }
    }

    public static T Run<T>(
        ICoreServerAPI? api,
        string operation,
        Func<T> action,
        Func<string, T> onFailure,
        Action<string, Exception>? onException = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(onFailure);

        try
        {
            return action();
        }
        catch (Exception exception)
        {
            Log(api, operation, exception);
            onException?.Invoke(operation, exception);
            return onFailure(BuildUserMessage(operation, exception));
        }
    }

    public static string BuildUserMessage(string operation, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);
        return $"[WA] {operation} failed: {exception.Message}";
    }

    private static void Log(ICoreServerAPI? api, string operation, Exception exception)
    {
        api?.Logger.Error("[WorldAudit] {0} failed: {1}", operation, exception);
    }
}
