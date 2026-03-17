using Vintagestory.API.Server;

namespace WorldAudit.Mod;

internal static class WorldAuditConfigLoader
{
    public static WorldAuditModConfig LoadOrCreate(ICoreServerAPI api, string configFileName)
    {
        try
        {
            var config = api.LoadModConfig<WorldAuditModConfig>(configFileName);
            if (config is { IsValid: true })
            {
                return config;
            }
        }
        catch (Exception exception)
        {
            api.Logger.Warning("[WorldAudit] Failed to load config '{0}': {1}", configFileName, exception.Message);
        }

        var fallback = new WorldAuditModConfig();
        api.StoreModConfig(fallback, configFileName);
        return fallback;
    }

    public static bool TryLoad(ICoreServerAPI api, string configFileName, out WorldAuditModConfig config, out string error)
    {
        try
        {
            var loaded = api.LoadModConfig<WorldAuditModConfig>(configFileName);
            if (loaded is { IsValid: true })
            {
                config = loaded;
                error = string.Empty;
                return true;
            }

            config = new WorldAuditModConfig();
            error = $"Config '{configFileName}' is missing or invalid.";
            return false;
        }
        catch (Exception exception)
        {
            config = new WorldAuditModConfig();
            error = $"Failed to load config '{configFileName}': {exception.Message}";
            return false;
        }
    }
}
