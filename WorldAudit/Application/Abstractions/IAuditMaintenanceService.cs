using WorldAudit.Application;

namespace WorldAudit.Application.Abstractions;

public interface IAuditMaintenanceService
{
    MaintenanceRunStatus? LastRun { get; }

    void RecordRun(MaintenanceRunStatus status);

    Task<SqliteMaintenanceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<int> GetBlockingRollbackJobCountAsync(CancellationToken cancellationToken = default);

    Task<PurgePreview> PreviewPurgeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    Task<PurgeResult> ExecutePurgeAsync(
        DateTimeOffset cutoff,
        SqliteCheckpointMode checkpointMode,
        bool optimizeAfterCheckpoint,
        CancellationToken cancellationToken = default);

    Task<SqliteCheckpointResult> RunCheckpointAsync(
        SqliteCheckpointMode mode,
        CancellationToken cancellationToken = default);

    Task RunOptimizeAsync(CancellationToken cancellationToken = default);

    Task<MaintenanceRunStatus> RecordMaintenanceRunAsync(
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        bool succeeded,
        int retentionDays,
        DateTimeOffset? cutoff,
        string summary,
        string? error = null,
        CancellationToken cancellationToken = default);
}
