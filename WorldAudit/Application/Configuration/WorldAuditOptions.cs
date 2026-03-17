namespace WorldAudit.Application.Configuration;

public sealed class WorldAuditOptions
{
    public string DatabasePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "worldaudit.db");

    public int WriterBatchSize { get; init; } = 128;

    public TimeSpan WriterMaxFlushDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public int DefaultPageSize { get; init; } = 8;

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

    public bool EnableNaturalCauseAudit { get; init; } = true;

    public bool EnableFireCauseProvider { get; init; } = true;

    public bool EnableExplosionCauseProvider { get; init; } = true;

    public bool EnableGravityCauseProvider { get; init; } = true;

    public bool EnableDecayCauseProvider { get; init; } = true;

    public bool EnableSystemCauseProvider { get; init; } = true;

    public bool EnableWorldgenCauseProvider { get; init; } = true;

    public bool VerboseDiagnostics { get; init; }
}
