namespace WorldAudit.Application;

public sealed record AuditWriterSnapshot(
    long PendingEvents,
    long PersistedEvents,
    int FlushCount,
    TimeSpan LastFlushDuration,
    string? LastError,
    bool IsPaused = false,
    int LastFlushBatchSize = 0,
    int LargestFlushBatchSize = 0,
    int SlowFlushCount = 0,
    TimeSpan SlowestFlushDuration = default);
