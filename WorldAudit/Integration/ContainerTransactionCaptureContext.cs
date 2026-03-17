using WorldAudit.Domain;

namespace WorldAudit.Integration;

public sealed record ContainerTransactionCaptureContext(
    string WorldId,
    BlockPosition Position,
    DateTimeOffset OccurredAt,
    string ActorName,
    string? ActorExternalId,
    AuditCause Cause,
    string InventoryType,
    string? ContainerLabel,
    IReadOnlyList<ContainerTransactionLineCapture> Lines,
    byte[]? BeforeSnapshot = null,
    byte[]? AfterSnapshot = null,
    int Flags = 0,
    long? SourceJobId = null);

public sealed record ContainerTransactionLineCapture(
    string ItemCode,
    int? SlotId,
    int QuantityDelta,
    int BeforeQuantity,
    int AfterQuantity,
    byte[]? StackBeforeSnapshot = null,
    byte[]? StackAfterSnapshot = null);
