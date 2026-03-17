namespace WorldAudit.Domain;

public sealed record ContainerHistoryQuery(
    string WorldId,
    BlockPosition Position,
    AuditTimeRange? TimeRange = null,
    IReadOnlyList<string>? IncludeCodes = null,
    IReadOnlyList<string>? ExcludeCodes = null,
    int Limit = 8);
