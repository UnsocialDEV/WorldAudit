using WorldAudit.Domain;

namespace WorldAudit.Application.Services;

public sealed record LookupFilters(
    AuditTimeRange? TimeRange,
    int? Radius,
    string? ActorName,
    AuditCause? Cause,
    BlockAuditAction? Action,
    int? PageSize,
    IReadOnlyList<string>? IncludeCodes = null,
    IReadOnlyList<string>? ExcludeCodes = null,
    bool IncludeBlocks = true,
    bool IncludeContainers = true);
