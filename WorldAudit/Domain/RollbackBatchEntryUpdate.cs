namespace WorldAudit.Domain;

public sealed record RollbackBatchEntryUpdate(
    long JobEntryId,
    RollbackJobEntryTargetType TargetType,
    RollbackJobEntryResult Result,
    string? ErrorText = null,
    BlockAuditEvent? BlockMutation = null,
    ContainerAuditTransaction? ContainerMutation = null);
