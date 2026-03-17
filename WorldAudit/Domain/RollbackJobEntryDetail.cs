namespace WorldAudit.Domain;

public sealed record RollbackJobEntryDetail(
    long JobEntryId,
    RollbackJobEntryTargetType TargetType,
    long SourceRecordId,
    BlockPosition Position,
    DateTimeOffset SourceOccurredAt,
    RollbackJobEntryResult Result,
    string? ErrorText);
