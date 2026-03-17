using WorldAudit.Application.Abstractions;

namespace WorldAudit.Application.Services;

public sealed class AuditMaintenanceService : IAuditMaintenanceService
{
    private readonly IAuditMaintenanceRepository _repository;
    private readonly object _sync = new();
    private MaintenanceRunStatus? _lastRun;

    public AuditMaintenanceService(IAuditMaintenanceRepository repository)
    {
        _repository = repository;
    }

    public MaintenanceRunStatus? LastRun
    {
        get
        {
            lock (_sync)
            {
                return _lastRun;
            }
        }
    }

    public void RecordRun(MaintenanceRunStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        SetLastRun(status);
    }

    public Task<SqliteMaintenanceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return _repository.GetSnapshotAsync(cancellationToken);
    }

    public Task<int> GetBlockingRollbackJobCountAsync(CancellationToken cancellationToken = default)
    {
        return _repository.GetBlockingRollbackJobCountAsync(cancellationToken);
    }

    public Task<PurgePreview> PreviewPurgeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        return _repository.PreviewPurgeAsync(cancellationToken: cancellationToken, cutoff: cutoff);
    }

    public Task<PurgeResult> ExecutePurgeAsync(
        DateTimeOffset cutoff,
        SqliteCheckpointMode checkpointMode,
        bool optimizeAfterCheckpoint,
        CancellationToken cancellationToken = default)
    {
        return _repository.ExecutePurgeAsync(cutoff, checkpointMode, optimizeAfterCheckpoint, cancellationToken);
    }

    public Task<SqliteCheckpointResult> RunCheckpointAsync(
        SqliteCheckpointMode mode,
        CancellationToken cancellationToken = default)
    {
        return _repository.RunCheckpointAsync(mode, cancellationToken);
    }

    public Task RunOptimizeAsync(CancellationToken cancellationToken = default)
    {
        return _repository.RunOptimizeAsync(cancellationToken);
    }

    public Task<MaintenanceRunStatus> RecordMaintenanceRunAsync(
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        bool succeeded,
        int retentionDays,
        DateTimeOffset? cutoff,
        string summary,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        var status = new MaintenanceRunStatus(startedAt, completedAt, succeeded, retentionDays, cutoff, summary, error);
        SetLastRun(status);
        return Task.FromResult(status);
    }

    private void SetLastRun(MaintenanceRunStatus status)
    {
        lock (_sync)
        {
            _lastRun = status;
        }
    }
}
