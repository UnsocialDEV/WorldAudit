using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

public sealed class SqliteAuditRepository : IAuditRepository
{
    private readonly SqliteAuditWriteStore _writeStore;
    private readonly SqliteAuditQueryStore _queryStore;
    private readonly SqliteAuditRollbackStore _rollbackStore;

    public SqliteAuditRepository(SqliteConnectionFactory connectionFactory, WorldAuditOptions options, AuditPerformanceDiagnostics? diagnostics = null)
    {
        var state = new SqliteAuditRepositoryState(connectionFactory, options, diagnostics);
        var resolver = new SqliteAuditLookupResolver(state);
        var reader = new SqliteAuditRecordReader();

        _writeStore = new SqliteAuditWriteStore(state, resolver);
        _queryStore = new SqliteAuditQueryStore(state, resolver, reader);
        _rollbackStore = new SqliteAuditRollbackStore(state, resolver, reader);
    }

    public Task WriteBlockEventsAsync(IReadOnlyList<BlockAuditEvent> events, CancellationToken cancellationToken = default)
        => _writeStore.WriteBlockEventsAsync(events, cancellationToken);

    public Task WriteContainerTransactionsAsync(IReadOnlyList<ContainerAuditTransaction> transactions, CancellationToken cancellationToken = default)
        => _writeStore.WriteContainerTransactionsAsync(transactions, cancellationToken);

    public Task<IReadOnlyList<BlockAuditEvent>> GetBlockHistoryAsync(BlockHistoryQuery query, CancellationToken cancellationToken = default)
        => _queryStore.GetBlockHistoryAsync(query, cancellationToken);

    public Task<IReadOnlyList<BlockAuditEvent>> LookupBlockEventsAsync(BlockLookupQuery query, CancellationToken cancellationToken = default)
        => _queryStore.LookupBlockEventsAsync(query, cancellationToken);

    public Task<IReadOnlyList<BlockAuditEvent>> SelectRollbackBlockEventsAsync(RollbackBlockSelectionQuery query, CancellationToken cancellationToken = default)
        => _queryStore.SelectRollbackBlockEventsAsync(query, cancellationToken);

    public Task<IReadOnlyList<ContainerAuditTransaction>> GetContainerHistoryAsync(ContainerHistoryQuery query, CancellationToken cancellationToken = default)
        => _queryStore.GetContainerHistoryAsync(query, cancellationToken);

    public Task<IReadOnlyList<ContainerAuditTransaction>> LookupContainerTransactionsAsync(ContainerLookupQuery query, CancellationToken cancellationToken = default)
        => _queryStore.LookupContainerTransactionsAsync(query, cancellationToken);

    public Task<IReadOnlyList<ContainerAuditTransaction>> SelectRollbackContainerTransactionsAsync(
        ContainerRollbackSelectionQuery query,
        CancellationToken cancellationToken = default)
        => _queryStore.SelectRollbackContainerTransactionsAsync(query, cancellationToken);

    public Task<RollbackJob> CreateRollbackJobAsync(
        RollbackJobCreateRequest request,
        IReadOnlyList<RollbackJobPlanEntryCreate> entries,
        CancellationToken cancellationToken = default)
        => _rollbackStore.CreateRollbackJobAsync(request, entries, cancellationToken);

    public Task<RollbackJob?> GetRollbackJobAsync(long jobId, CancellationToken cancellationToken = default)
        => _rollbackStore.GetRollbackJobAsync(jobId, cancellationToken);

    public Task<IReadOnlyList<RollbackJob>> GetRollbackJobsAsync(int limit = 10, CancellationToken cancellationToken = default)
        => _rollbackStore.GetRollbackJobsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<BlockAuditEvent>> GetRollbackJobSourceBlockEventsAsync(
        long jobId,
        bool appliedOnly,
        CancellationToken cancellationToken = default)
        => _rollbackStore.GetRollbackJobSourceBlockEventsAsync(jobId, appliedOnly, cancellationToken);

    public Task<IReadOnlyList<ContainerAuditTransaction>> GetRollbackJobSourceContainerTransactionsAsync(
        long jobId,
        bool appliedOnly,
        CancellationToken cancellationToken = default)
        => _rollbackStore.GetRollbackJobSourceContainerTransactionsAsync(jobId, appliedOnly, cancellationToken);

    public Task<IReadOnlyList<RollbackPlanEntry>> GetRollbackPlanEntriesAsync(long jobId, CancellationToken cancellationToken = default)
        => _rollbackStore.GetRollbackPlanEntriesAsync(jobId, cancellationToken);

    public Task<IReadOnlyList<RollbackBlockPlanEntry>> GetRollbackBlockPlanEntriesAsync(long jobId, CancellationToken cancellationToken = default)
        => _rollbackStore.GetRollbackBlockPlanEntriesAsync(jobId, cancellationToken);

    public Task<IReadOnlyList<RollbackJobEntryDetail>> GetRollbackJobEntryDetailsAsync(
        long jobId,
        RollbackJobEntryResult result,
        int limit = 10,
        CancellationToken cancellationToken = default)
        => _rollbackStore.GetRollbackJobEntryDetailsAsync(jobId, result, limit, cancellationToken);

    public Task UpdateRollbackJobStateAsync(
        long jobId,
        RollbackJobState state,
        DateTimeOffset? queuedAt = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        string? lastError = null,
        CancellationToken cancellationToken = default)
        => _rollbackStore.UpdateRollbackJobStateAsync(jobId, state, queuedAt, startedAt, completedAt, lastError, cancellationToken);

    public Task RecordRollbackBatchAsync(long jobId, IReadOnlyList<RollbackBatchEntryUpdate> updates, CancellationToken cancellationToken = default)
        => _rollbackStore.RecordRollbackBatchAsync(jobId, updates, cancellationToken);
}
