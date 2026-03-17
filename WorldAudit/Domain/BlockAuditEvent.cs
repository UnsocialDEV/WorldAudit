namespace WorldAudit.Domain;

public sealed record BlockAuditEvent(
    long? Id,
    string WorldId,
    BlockPosition Position,
    DateTimeOffset OccurredAt,
    AuditActor Actor,
    AuditCause Cause,
    BlockAuditAction Action,
    string? OldBlockCode,
    string? NewBlockCode,
    byte[]? OldBlockEntitySnapshot = null,
    byte[]? NewBlockEntitySnapshot = null,
    int Flags = 0,
    long? SourceJobId = null,
    long? SourceEventId = null);
