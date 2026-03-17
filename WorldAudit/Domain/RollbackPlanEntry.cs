namespace WorldAudit.Domain;

public abstract record RollbackPlanEntry(
    long JobEntryId,
    long JobId,
    RollbackJobEntryTargetType TargetType,
    long SourceRecordId,
    string WorldId,
    RollbackJobOperation Operation,
    BlockPosition Position,
    ChunkPosition Chunk,
    DateTimeOffset SourceOccurredAt,
    int ExecutionOrder);
