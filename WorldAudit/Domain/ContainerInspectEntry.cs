namespace WorldAudit.Domain;

internal sealed record ContainerInspectEntry(long DetailId, string? VisitId, string Label,
    string Actor, AuditCause Cause, DateTimeOffset FirstChange, DateTimeOffset LastChange,
    long Transactions, IReadOnlyList<ContainerItemTotal> Items, BlockPosition Position = default);

internal sealed record ContainerItemTotal(string Code, long Delta);
