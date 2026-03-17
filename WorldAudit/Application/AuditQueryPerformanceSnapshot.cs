namespace WorldAudit.Application;

public sealed record AuditQueryPerformanceSnapshot(
    long QueryCount,
    int SlowQueryCount,
    string? LastOperation,
    TimeSpan LastDuration,
    string? SlowestOperation,
    TimeSpan SlowestDuration);
