using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Domain;

namespace WorldAudit.Application.Services;

public sealed class RollbackPlanner
{
    private readonly IAuditRepository _repository;
    private readonly WorldAuditOptions _options;

    internal SemaphoreSlim HistoryAccess { get; } = new(1, 1);
    private readonly IAuditWriter? _writer;

    public RollbackPlanner(IAuditRepository repository, WorldAuditOptions options, IAuditWriter? writer = null)
    {
        _writer = writer;
        _repository = repository;
        _options = options;
    }

    public Task<RollbackJob> PreviewRollbackAsync(
        string worldId,
        BlockPosition center,
        LookupFilters filters,
        AuditActor requestedBy,
        string filterSummary,
        CancellationToken cancellationToken = default)
    {
        return PreviewAsync(
            worldId,
            center,
            filters,
            requestedBy,
            filterSummary,
            RollbackJobOperation.Rollback,
            cancellationToken);
    }

    public Task<RollbackJob> PreviewRestoreAsync(
        string worldId,
        BlockPosition center,
        LookupFilters filters,
        AuditActor requestedBy,
        string filterSummary,
        CancellationToken cancellationToken = default)
    {
        return PreviewAsync(
            worldId,
            center,
            filters,
            requestedBy,
            filterSummary,
            RollbackJobOperation.Restore,
            cancellationToken);
    }

    public async Task<RollbackJob> PreviewUndoAsync(
        long sourceJobId,
        AuditActor requestedBy,
        CancellationToken cancellationToken = default)
    {
        await HistoryAccess.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var access = new HistoryAccessLease(HistoryAccess);
        var sourceJob = await _repository.GetRollbackJobAsync(sourceJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job #{sourceJobId} was not found.");
        if (!string.IsNullOrWhiteSpace(_options.WorldId) && !string.Equals(_options.WorldId, sourceJob.WorldId, StringComparison.Ordinal))
            throw new InvalidOperationException("This job belongs to another world.");

        if (sourceJob.State is not (RollbackJobState.Completed or RollbackJobState.CompletedWithConflicts))
        {
            throw new InvalidOperationException($"Job #{sourceJobId} must be completed before it can be undone.");
        }

        var inverseOperation = Invert(sourceJob.Operation);
        return await _repository.FreezeRollbackPreviewAsync(new(sourceJob.WorldId, requestedBy, inverseOperation,
            $"undo job #{sourceJobId}", default, null, _options.RollbackPreviewLimit, sourceJobId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<RollbackJob> PreviewAsync(
        string worldId,
        BlockPosition center,
        LookupFilters filters,
        AuditActor requestedBy,
        string filterSummary,
        RollbackJobOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldId);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(requestedBy);

        await HistoryAccess.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var access = new HistoryAccessLease(HistoryAccess);
        if (_writer is not null)
        {
            try
            {
                await _writer.FlushAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException("Could not persist recent audit history. Check /wa status and retry the preview.", exception);
            }
        }

        if (_writer?.Snapshot().RejectedEvents is > 0 and var missing)
            filterSummary += $" | Warning: audit history may be incomplete ({missing} lost records since startup).";

        var summary = string.IsNullOrWhiteSpace(filterSummary) ? $"{operation} preview" : filterSummary;
        return await _repository.FreezeRollbackPreviewAsync(new(worldId, requestedBy, operation, summary, center,
            filters, _options.RollbackPreviewLimit), cancellationToken).ConfigureAwait(false);
    }

    private static RollbackJobOperation Invert(RollbackJobOperation operation)
    {
        return operation == RollbackJobOperation.Rollback
            ? RollbackJobOperation.Restore
            : RollbackJobOperation.Rollback;
    }

}
