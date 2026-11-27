using WorldAudit.Domain;

namespace WorldAudit.Application.Services;

public sealed record RollbackPreviewSelection(string World, AuditActor RequestedBy, RollbackJobOperation Operation,
    string Summary, BlockPosition Center, LookupFilters? Filters, int BatchSize, long? UndoJobId = null);
