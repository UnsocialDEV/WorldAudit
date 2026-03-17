namespace WorldAudit.Domain;

public sealed record RollbackJob(
    long Id,
    string WorldId,
    AuditActor RequestedBy,
    RollbackJobOperation Operation,
    string FilterSummary,
    RollbackJobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int PlannedBlockCount,
    int PlannedContainerCount,
    int PlannedChunkCount,
    int AppliedBlockCount,
    int AppliedContainerCount,
    int ConflictBlockCount,
    int ConflictContainerCount,
    int FailedBlockCount,
    int FailedContainerCount,
    DateTimeOffset? OldestTargetOccurredAt,
    string? LastError);
