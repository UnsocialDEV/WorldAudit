using System.Text.Json.Serialization;
using Vintagestory.API.Common;
using WorldAudit.Application.Configuration;

namespace WorldAudit.Mod;

public sealed class WorldAuditModConfig
{
    private const string DefaultDiscordWebhookUrl = "https://discord.com/api/webhooks/1481074309488312482/O1iclCQFeZy-YQjjqlwEL0ZPXXKVkTNdXqw1ZdnoAFYSjrtNnqhh4JktT7YlyQR7liRc";
    private const string DefaultDiscordChannelId = "1481074288600682716";

    public string DatabasePath { get; init; } = "worldaudit.db";

    public int WriterBatchSize { get; init; } = 128;

    public int WriterMaxFlushDelayMilliseconds { get; init; } = 250;

    public int DefaultPageSize { get; init; } = 8;

    public int DefaultNearRadius { get; init; } = 5;

    public int ChunkSize { get; init; } = 32;

    public int SqliteBusyTimeoutMilliseconds { get; init; } = 5_000;

    public int RollbackPreviewLimit { get; init; } = 5_000;

    public int RollbackMaxBlocksPerTick { get; init; } = 100;

    public int RollbackTickIntervalMilliseconds { get; init; } = 50;

    public int RetentionDays { get; init; }

    public int CheckpointIntervalMinutes { get; init; } = 30;

    public int CheckpointWalSizeMegabytes { get; init; } = 64;

    public int OptimizeIntervalHours { get; init; } = 24;

    public int SlowQueryThresholdMilliseconds { get; init; } = 25;

    public int SlowFlushThresholdMilliseconds { get; init; } = 50;

    public bool EnableBlockAudit { get; init; } = true;

    public bool EnableContainerAudit { get; init; } = true;

    public bool EnableNaturalCauseAudit { get; init; } = true;

    public bool EnableFireCauseProvider { get; init; } = true;

    public bool EnableExplosionCauseProvider { get; init; } = true;

    public bool EnableGravityCauseProvider { get; init; } = true;

    public bool EnableDecayCauseProvider { get; init; } = true;

    public bool EnableSystemCauseProvider { get; init; } = true;

    public bool EnableWorldgenCauseProvider { get; init; } = true;

    public bool VerboseDiagnostics { get; init; }

    public bool EnableDiscordTelemetry { get; init; } = true;

    public string DiscordWebhookUrl { get; init; } = DefaultDiscordWebhookUrl;

    public string DiscordChannelId { get; init; } = DefaultDiscordChannelId;

    public int DiscordTelemetryTimeoutSeconds { get; init; } = 5;

    public bool IncludeOnlinePlayerNamesInTelemetry { get; init; } = true;

    [JsonIgnore]
    public bool IsValid =>
        WriterBatchSize > 0
        && WriterMaxFlushDelayMilliseconds > 0
        && DefaultPageSize > 0
        && DefaultNearRadius > 0
        && ChunkSize > 0
        && RollbackPreviewLimit > 0
        && RollbackMaxBlocksPerTick > 0
        && RollbackTickIntervalMilliseconds > 0
        && RetentionDays >= 0
        && CheckpointIntervalMinutes > 0
        && CheckpointWalSizeMegabytes > 0
        && OptimizeIntervalHours > 0
        && SlowQueryThresholdMilliseconds > 0
        && SlowFlushThresholdMilliseconds > 0
        && DiscordTelemetryTimeoutSeconds > 0
        && (!EnableDiscordTelemetry
            || (!string.IsNullOrWhiteSpace(DiscordWebhookUrl)
                && !string.IsNullOrWhiteSpace(DiscordChannelId)));

    public WorldAuditOptions ToRuntimeOptions(ICoreAPICommon? api = null)
    {
        return new WorldAuditOptions
        {
            DatabasePath = ResolveDatabasePath(DatabasePath, api),
            WriterBatchSize = WriterBatchSize,
            WriterMaxFlushDelay = TimeSpan.FromMilliseconds(WriterMaxFlushDelayMilliseconds),
            DefaultPageSize = DefaultPageSize,
            ChunkSize = ChunkSize,
            SqliteBusyTimeoutMilliseconds = SqliteBusyTimeoutMilliseconds,
            RollbackPreviewLimit = RollbackPreviewLimit,
            RollbackMaxBlocksPerTick = RollbackMaxBlocksPerTick,
            RollbackTickIntervalMilliseconds = RollbackTickIntervalMilliseconds,
            RetentionDays = RetentionDays,
            CheckpointIntervalMinutes = CheckpointIntervalMinutes,
            CheckpointWalSizeMegabytes = CheckpointWalSizeMegabytes,
            OptimizeIntervalHours = OptimizeIntervalHours,
            SlowQueryThresholdMilliseconds = SlowQueryThresholdMilliseconds,
            SlowFlushThresholdMilliseconds = SlowFlushThresholdMilliseconds,
            EnableNaturalCauseAudit = EnableNaturalCauseAudit,
            EnableFireCauseProvider = EnableFireCauseProvider,
            EnableExplosionCauseProvider = EnableExplosionCauseProvider,
            EnableGravityCauseProvider = EnableGravityCauseProvider,
            EnableDecayCauseProvider = EnableDecayCauseProvider,
            EnableSystemCauseProvider = EnableSystemCauseProvider,
            EnableWorldgenCauseProvider = EnableWorldgenCauseProvider,
            VerboseDiagnostics = VerboseDiagnostics
        };
    }

    private static string ResolveDatabasePath(string configuredPath, ICoreAPICommon? api)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var basePath = api?.GetOrCreateDataPath("WorldAudit") ?? AppContext.BaseDirectory;
        return Path.Combine(basePath, configuredPath);
    }
}
