namespace WorldAudit.Application;

public sealed record AuditCheckpointStatus(
    DateTimeOffset CompletedAt,
    SqliteCheckpointMode Mode,
    string Reason,
    TimeSpan Duration,
    long WalSizeBytesBefore,
    long WalSizeBytesAfter);
