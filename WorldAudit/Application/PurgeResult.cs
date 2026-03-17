namespace WorldAudit.Application;

public sealed record PurgeResult(
    DateTimeOffset Cutoff,
    int DeletedBlockEventCount,
    int DeletedContainerTransactionCount,
    int DeletedRollbackJobCount,
    bool CheckpointRan,
    bool OptimizeRan,
    SqliteCheckpointMode? CheckpointMode = null,
    long WalSizeBytesBeforeCheckpoint = 0,
    long WalSizeBytesAfterCheckpoint = 0,
    TimeSpan CheckpointDuration = default,
    TimeSpan OptimizeDuration = default)
{
    public int TotalDeletedCount => DeletedBlockEventCount + DeletedContainerTransactionCount + DeletedRollbackJobCount;
}
