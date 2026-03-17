namespace WorldAudit.Application;

public sealed record AuditStatusSnapshot(
    string DatabasePath,
    string SqliteVersion,
    long WalSizeBytes,
    bool IsConsumerPaused,
    AuditWriterSnapshot WriterSnapshot,
    int ActiveRollbackJobCount,
    int RetentionDays,
    MaintenanceRunStatus? LastMaintenanceRun,
    AuditQueryPerformanceSnapshot QueryPerformance,
    AuditCheckpointPolicySnapshot CheckpointPolicy,
    AuditCheckpointStatus? LastCheckpoint,
    AuditOptimizeStatus? LastOptimize,
    bool IsWalThresholdExceeded);
