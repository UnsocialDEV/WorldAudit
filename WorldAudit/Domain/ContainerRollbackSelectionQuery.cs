namespace WorldAudit.Domain;

public sealed record ContainerRollbackSelectionQuery(
    string WorldId,
    BlockPosition Center,
    int Radius,
    AuditTimeRange? TimeRange = null,
    string? ActorName = null,
    AuditCause? Cause = null,
    IReadOnlyList<string>? IncludeCodes = null,
    IReadOnlyList<string>? ExcludeCodes = null,
    int Limit = 5_000);
