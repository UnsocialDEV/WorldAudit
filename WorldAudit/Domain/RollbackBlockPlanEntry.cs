namespace WorldAudit.Domain;

public sealed record RollbackBlockPlanEntry(
    long JobEntryId,
    long JobId,
    long SourceEventId,
    string WorldId,
    RollbackJobOperation Operation,
    BlockPosition Position,
    ChunkPosition Chunk,
    DateTimeOffset SourceOccurredAt,
    string? TargetBlockCode,
    byte[]? TargetBlockEntitySnapshot,
    int ExecutionOrder)
    : RollbackPlanEntry(
        JobEntryId,
        JobId,
        RollbackJobEntryTargetType.Block,
        SourceEventId,
        WorldId,
        Operation,
        Position,
        Chunk,
        SourceOccurredAt,
        ExecutionOrder);
