namespace WorldAudit.Domain;

public sealed record RollbackJobPlanEntryCreate(
    RollbackJobEntryTargetType TargetType,
    long SourceRecordId,
    int ExecutionOrder);
