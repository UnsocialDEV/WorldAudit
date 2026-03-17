namespace WorldAudit.Domain;

public sealed record RollbackContainerPlanEntry(
    long JobEntryId,
    long JobId,
    long SourceTransactionId,
    string WorldId,
    RollbackJobOperation Operation,
    BlockPosition Position,
    ChunkPosition Chunk,
    DateTimeOffset SourceOccurredAt,
    string InventoryType,
    string? ContainerLabel,
    byte[]? ExpectedSnapshot,
    byte[]? TargetSnapshot,
    int ExecutionOrder)
    : RollbackPlanEntry(
        JobEntryId,
        JobId,
        RollbackJobEntryTargetType.Container,
        SourceTransactionId,
        WorldId,
        Operation,
        Position,
        Chunk,
        SourceOccurredAt,
        ExecutionOrder);
