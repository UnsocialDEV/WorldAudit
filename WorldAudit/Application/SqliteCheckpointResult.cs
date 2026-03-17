namespace WorldAudit.Application;

public sealed record SqliteCheckpointResult(
    SqliteCheckpointMode Mode,
    long WalSizeBytesBefore,
    long WalSizeBytesAfter);
