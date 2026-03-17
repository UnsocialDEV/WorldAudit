namespace WorldAudit.Domain;

public sealed record ContainerAuditLine(
    long? Id,
    string ItemCode,
    int? SlotId,
    int QuantityDelta,
    int BeforeQuantity,
    int AfterQuantity,
    byte[]? StackBeforeSnapshot = null,
    byte[]? StackAfterSnapshot = null);
