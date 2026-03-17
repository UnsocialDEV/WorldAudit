namespace WorldAudit.Domain;

public sealed record ContainerAuditTransaction(
    long? Id,
    string WorldId,
    BlockPosition Position,
    DateTimeOffset OccurredAt,
    AuditActor Actor,
    AuditCause Cause,
    string InventoryType,
    string? ContainerLabel,
    byte[]? BeforeSnapshot,
    byte[]? AfterSnapshot,
    IReadOnlyList<ContainerAuditLine> Lines,
    int Flags = 0,
    long? SourceJobId = null);
