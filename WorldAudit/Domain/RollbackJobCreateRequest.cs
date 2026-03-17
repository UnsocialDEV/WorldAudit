namespace WorldAudit.Domain;

public sealed record RollbackJobCreateRequest(
    string WorldId,
    AuditActor RequestedBy,
    RollbackJobOperation Operation,
    string FilterSummary,
    RollbackJobState State,
    int PlannedBlockCount,
    int PlannedContainerCount,
    int PlannedChunkCount,
    DateTimeOffset? OldestTargetOccurredAt);
