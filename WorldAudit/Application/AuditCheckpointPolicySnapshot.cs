namespace WorldAudit.Application;

public sealed record AuditCheckpointPolicySnapshot(
    TimeSpan CheckpointInterval,
    long WalSizeThresholdBytes,
    TimeSpan OptimizeInterval);
