using System.Reflection;
using System.Runtime.InteropServices;
using WorldAudit.Application;

namespace WorldAudit.Mod.Telemetry;

internal sealed class WorldAuditTelemetryPayloadFactory
{
    private const int InfoColor = 0x3BA55D;
    private const int ErrorColor = 0xED4245;
    private const int DiscordDescriptionLimit = 4096;
    private const int DiscordFieldLimit = 1024;
    private const int MaxFields = 25;

    public DiscordWebhookMessage CreateStartupMessage(
        WorldAuditServerEndpointSnapshot endpoint,
        WorldAuditModConfig config,
        WorldAuditRuntime? runtime,
        string worldId,
        string worldName,
        WorldAuditOnlinePlayerSnapshot onlinePlayers)
    {
        return new DiscordWebhookMessage(
            Content: $"WorldAudit loaded for world `{Trim(worldId, 128)}`.",
            Username: "WorldAudit Tracker",
            Embeds:
            [
                new DiscordWebhookEmbed(
                    Title: "WorldAudit Startup",
                    Description: Trim(BuildStartupDescription(config, worldId, worldName), DiscordDescriptionLimit),
                    Color: InfoColor,
                    Fields: BuildStartupFields(endpoint, config, runtime, onlinePlayers),
                    Timestamp: DateTimeOffset.UtcNow.ToString("O"))
            ]);
    }

    public DiscordWebhookMessage CreateErrorMessage(
        WorldAuditServerEndpointSnapshot endpoint,
        string operation,
        Exception exception,
        WorldAuditRuntime? runtime,
        string worldId,
        string worldName,
        WorldAuditOnlinePlayerSnapshot onlinePlayers,
        string? detail = null)
    {
        return new DiscordWebhookMessage(
            Content: $"WorldAudit detected a failure during `{Trim(operation, 128)}`.",
            Username: "WorldAudit Tracker",
            Embeds:
            [
                new DiscordWebhookEmbed(
                    Title: "WorldAudit Error",
                    Description: Trim(BuildExceptionSummary(exception, detail), DiscordDescriptionLimit),
                    Color: ErrorColor,
                    Fields: BuildErrorFields(endpoint, operation, runtime, worldId, worldName, onlinePlayers),
                    Timestamp: DateTimeOffset.UtcNow.ToString("O"))
            ]);
    }

    public DiscordWebhookMessage CreateMaintenanceFailureMessage(
        WorldAuditServerEndpointSnapshot endpoint,
        MaintenanceRunStatus status,
        WorldAuditRuntime? runtime,
        string worldId,
        string worldName,
        WorldAuditOnlinePlayerSnapshot onlinePlayers)
    {
        return new DiscordWebhookMessage(
            Content: "WorldAudit maintenance failed.",
            Username: "WorldAudit Tracker",
            Embeds:
            [
                new DiscordWebhookEmbed(
                    Title: "WorldAudit Maintenance Failure",
                    Description: Trim(BuildMaintenanceDescription(status), DiscordDescriptionLimit),
                    Color: ErrorColor,
                    Fields: BuildErrorFields(endpoint, "maintenance", runtime, worldId, worldName, onlinePlayers),
                    Timestamp: DateTimeOffset.UtcNow.ToString("O"))
            ]);
    }

    private static IReadOnlyList<DiscordWebhookField> BuildStartupFields(
        WorldAuditServerEndpointSnapshot endpoint,
        WorldAuditModConfig config,
        WorldAuditRuntime? runtime,
        WorldAuditOnlinePlayerSnapshot onlinePlayers)
    {
        var fields = new List<DiscordWebhookField>
        {
            CreateField("Server Endpoint", endpoint.DisplayValue),
            CreateField("Mod Version", GetAssemblyVersion()),
            CreateField(".NET", RuntimeInformation.FrameworkDescription),
            CreateField("OS", RuntimeInformation.OSDescription),
            CreateField("Process", $"{Environment.ProcessId} / {RuntimeInformation.ProcessArchitecture}"),
            CreateField("Machine", Environment.MachineName),
            CreateField("Audit Flags", BuildAuditFlagSummary(config)),
            CreateField("Online Players", BuildOnlinePlayerSummary(onlinePlayers)),
            CreateField("Runtime", BuildRuntimeSummary(runtime)),
            CreateField("Database", runtime?.Options.DatabasePath ?? config.DatabasePath)
        };

        return fields.Take(MaxFields).ToArray();
    }

    private static IReadOnlyList<DiscordWebhookField> BuildErrorFields(
        WorldAuditServerEndpointSnapshot endpoint,
        string operation,
        WorldAuditRuntime? runtime,
        string worldId,
        string worldName,
        WorldAuditOnlinePlayerSnapshot onlinePlayers)
    {
        var fields = new List<DiscordWebhookField>
        {
            CreateField("Server Endpoint", endpoint.DisplayValue),
            CreateField("Operation", operation),
            CreateField("World", $"{worldName} ({worldId})"),
            CreateField("Runtime", BuildRuntimeSummary(runtime)),
            CreateField("Online Players", BuildOnlinePlayerSummary(onlinePlayers)),
            CreateField("Machine", Environment.MachineName),
            CreateField("Process", $"{Environment.ProcessId} / {RuntimeInformation.ProcessArchitecture}")
        };

        return fields.Take(MaxFields).ToArray();
    }

    private static string BuildStartupDescription(WorldAuditModConfig config, string worldId, string worldName)
    {
        return string.Join(
            Environment.NewLine,
            [
                $"World: {worldName} ({worldId})",
                $"Started: {DateTimeOffset.UtcNow:O}",
                $"Writer batch/max delay: {config.WriterBatchSize}/{config.WriterMaxFlushDelayMilliseconds}ms",
                $"Rollback tick/max blocks: {config.RollbackTickIntervalMilliseconds}ms/{config.RollbackMaxBlocksPerTick}",
                $"Retention/checkpoint/optimize: {config.RetentionDays}d, {config.CheckpointIntervalMinutes}m, {config.OptimizeIntervalHours}h"
            ]);
    }

    private static string BuildExceptionSummary(Exception exception, string? detail)
    {
        var summary = string.Join(
            Environment.NewLine,
            [
                $"Type: {exception.GetType().FullName}",
                $"Message: {exception.Message}",
                $"Detail: {detail ?? "n/a"}",
                $"Inner: {FlattenInnerExceptions(exception)}",
                "Stack:",
                exception.ToString()
            ]);

        return summary;
    }

    private static string BuildMaintenanceDescription(MaintenanceRunStatus status)
    {
        return string.Join(
            Environment.NewLine,
            [
                $"Started: {status.StartedAt:O}",
                $"Completed: {status.CompletedAt:O}",
                $"Summary: {status.Summary}",
                $"Error: {status.Error ?? "n/a"}",
                $"Retention Days: {status.RetentionDays}",
                $"Cutoff: {status.Cutoff?.ToString("O") ?? "n/a"}"
            ]);
    }

    private static string BuildAuditFlagSummary(WorldAuditModConfig config)
    {
        return string.Join(
            ", ",
            [
                $"block={config.EnableBlockAudit}",
                $"container={config.EnableContainerAudit}",
                $"natural={config.EnableNaturalCauseAudit}",
                $"fire={config.EnableFireCauseProvider}",
                $"explosion={config.EnableExplosionCauseProvider}",
                $"gravity={config.EnableGravityCauseProvider}",
                $"decay={config.EnableDecayCauseProvider}",
                $"system={config.EnableSystemCauseProvider}",
                $"worldgen={config.EnableWorldgenCauseProvider}"
            ]);
    }

    private static string BuildRuntimeSummary(WorldAuditRuntime? runtime)
    {
        if (runtime is null)
        {
            return "Runtime not initialized.";
        }

        return $"paused={runtime.IsConsumerPaused}, rollbackJobs={runtime.RollbackCoordinator.GetActiveJobCount()}, db={runtime.Options.DatabasePath}";
    }

    private static string BuildOnlinePlayerSummary(WorldAuditOnlinePlayerSnapshot onlinePlayers)
    {
        if (onlinePlayers.Count == 0)
        {
            return "0 online";
        }

        if (onlinePlayers.Names.Count == 0)
        {
            return $"{onlinePlayers.Count} online";
        }

        return Trim($"{onlinePlayers.Count} online: {string.Join(", ", onlinePlayers.Names)}", DiscordFieldLimit);
    }

    private static string GetAssemblyVersion()
    {
        var assembly = typeof(WorldAuditModSystem).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static string FlattenInnerExceptions(Exception exception)
    {
        var messages = new List<string>();
        var current = exception.InnerException;
        while (current is not null && messages.Count < 5)
        {
            messages.Add($"{current.GetType().Name}: {current.Message}");
            current = current.InnerException;
        }

        return messages.Count == 0 ? "none" : string.Join(" | ", messages);
    }

    private static DiscordWebhookField CreateField(string name, string value)
    {
        return new DiscordWebhookField(Trim(name, 256), Trim(value, DiscordFieldLimit));
    }

    private static string Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "n/a";
        }

        return value.Length <= maxLength
            ? value
            : value[..(maxLength - 3)] + "...";
    }
}
