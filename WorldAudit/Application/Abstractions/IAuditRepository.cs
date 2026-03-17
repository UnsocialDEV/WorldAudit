using WorldAudit.Domain;

namespace WorldAudit.Application.Abstractions;

public interface IAuditRepository
{
    Task WriteBlockEventsAsync(IReadOnlyList<BlockAuditEvent> events, CancellationToken cancellationToken = default);

    Task WriteContainerTransactionsAsync(IReadOnlyList<ContainerAuditTransaction> transactions, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BlockAuditEvent>> GetBlockHistoryAsync(BlockHistoryQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BlockAuditEvent>> LookupBlockEventsAsync(BlockLookupQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BlockAuditEvent>> SelectRollbackBlockEventsAsync(RollbackBlockSelectionQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContainerAuditTransaction>> GetContainerHistoryAsync(ContainerHistoryQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContainerAuditTransaction>> LookupContainerTransactionsAsync(ContainerLookupQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContainerAuditTransaction>> SelectRollbackContainerTransactionsAsync(
        ContainerRollbackSelectionQuery query,
        CancellationToken cancellationToken = default);

    Task<RollbackJob> CreateRollbackJobAsync(
        RollbackJobCreateRequest request,
        IReadOnlyList<RollbackJobPlanEntryCreate> entries,
        CancellationToken cancellationToken = default);

    Task<RollbackJob?> GetRollbackJobAsync(long jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RollbackJob>> GetRollbackJobsAsync(int limit = 10, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BlockAuditEvent>> GetRollbackJobSourceBlockEventsAsync(
        long jobId,
        bool appliedOnly,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContainerAuditTransaction>> GetRollbackJobSourceContainerTransactionsAsync(
        long jobId,
        bool appliedOnly,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RollbackPlanEntry>> GetRollbackPlanEntriesAsync(long jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RollbackBlockPlanEntry>> GetRollbackBlockPlanEntriesAsync(long jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RollbackJobEntryDetail>> GetRollbackJobEntryDetailsAsync(
        long jobId,
        RollbackJobEntryResult result,
        int limit = 10,
        CancellationToken cancellationToken = default);

    Task UpdateRollbackJobStateAsync(
        long jobId,
        RollbackJobState state,
        DateTimeOffset? queuedAt = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        string? lastError = null,
        CancellationToken cancellationToken = default);

    Task RecordRollbackBatchAsync(long jobId, IReadOnlyList<RollbackBatchEntryUpdate> updates, CancellationToken cancellationToken = default);
}
