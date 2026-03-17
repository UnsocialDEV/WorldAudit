using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Domain;

namespace WorldAudit.Application.Services;

public sealed class BlockAuditQueryService
{
    private readonly IAuditRepository _repository;
    private readonly WorldAuditOptions _options;

    public BlockAuditQueryService(IAuditRepository repository, WorldAuditOptions options)
    {
        _repository = repository;
        _options = options;
    }

    public Task<IReadOnlyList<BlockAuditEvent>> GetHistoryAsync(
        string worldId,
        BlockPosition position,
        AuditTimeRange? timeRange = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var query = new BlockHistoryQuery(
            worldId,
            position,
            timeRange,
            IncludeCodes: null,
            ExcludeCodes: null,
            NormalizeLimit(limit));

        return _repository.GetBlockHistoryAsync(query, cancellationToken);
    }

    public Task<IReadOnlyList<BlockAuditEvent>> LookupAsync(
        string worldId,
        BlockPosition center,
        LookupFilters filters,
        CancellationToken cancellationToken = default)
    {
        var query = new BlockLookupQuery(
            worldId,
            center,
            filters.Radius ?? 5,
            filters.TimeRange,
            filters.ActorName,
            filters.Cause,
            filters.Action,
            filters.IncludeCodes,
            filters.ExcludeCodes,
            NormalizeLimit(filters.PageSize));

        return _repository.LookupBlockEventsAsync(query, cancellationToken);
    }

    private int NormalizeLimit(int? limit)
    {
        return limit is > 0 ? limit.Value : _options.DefaultPageSize;
    }
}
