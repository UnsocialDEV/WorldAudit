namespace WorldAudit.Domain;

public sealed record BlockLookupQuery(
    string WorldId,
    BlockPosition Center,
    int Radius,
    AuditTimeRange? TimeRange = null,
    string? ActorName = null,
    AuditCause? Cause = null,
    BlockAuditAction? Action = null,
    IReadOnlyList<string>? IncludeCodes = null,
    IReadOnlyList<string>? ExcludeCodes = null,
    int Limit = 50);
