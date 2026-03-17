namespace WorldAudit.Application;

public sealed record SqliteMaintenanceSnapshot(
    string SqliteVersion,
    long WalSizeBytes);
