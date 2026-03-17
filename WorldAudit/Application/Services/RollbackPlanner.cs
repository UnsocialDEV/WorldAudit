using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Domain;

namespace WorldAudit.Application.Services;

public sealed class RollbackPlanner
{
    private readonly IAuditRepository _repository;
    private readonly WorldAuditOptions _options;

    public RollbackPlanner(IAuditRepository repository, WorldAuditOptions options)
    {
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
        var sourceJob = await _repository.GetRollbackJobAsync(sourceJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job #{sourceJobId} was not found.");

        if (sourceJob.State is not (RollbackJobState.Completed or RollbackJobState.CompletedWithConflicts))
        {
            throw new InvalidOperationException($"Job #{sourceJobId} must be completed before it can be undone.");
        }

        var inverseOperation = Invert(sourceJob.Operation);
        var sourceEvents = await _repository.GetRollbackJobSourceBlockEventsAsync(
            sourceJobId,
            appliedOnly: true,
            cancellationToken).ConfigureAwait(false);
        var sourceTransactions = await _repository.GetRollbackJobSourceContainerTransactionsAsync(
            sourceJobId,
            appliedOnly: true,
            cancellationToken).ConfigureAwait(false);

        return await CreatePreviewJobAsync(
            sourceJob.WorldId,
            requestedBy,
            $"undo job #{sourceJobId}",
            inverseOperation,
            sourceEvents,
            sourceTransactions,
            cancellationToken).ConfigureAwait(false);
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

        var selectionQuery = new RollbackBlockSelectionQuery(
            worldId,
            center,
            filters.Radius ?? 5,
            filters.TimeRange,
            filters.ActorName,
            filters.Cause,
            filters.Action,
            filters.IncludeCodes,
            filters.ExcludeCodes,
            _options.RollbackPreviewLimit);

        var selectedEvents = filters.IncludeBlocks
            ? await _repository.SelectRollbackBlockEventsAsync(selectionQuery, cancellationToken).ConfigureAwait(false)
            : Array.Empty<BlockAuditEvent>();
        var selectedTransactions = filters.IncludeContainers
            ? await _repository.SelectRollbackContainerTransactionsAsync(
                new ContainerRollbackSelectionQuery(
                    worldId,
                    center,
                    filters.Radius ?? 5,
                    filters.TimeRange,
                    filters.ActorName,
                    filters.Cause,
                    filters.IncludeCodes,
                    filters.ExcludeCodes,
                    _options.RollbackPreviewLimit),
                cancellationToken).ConfigureAwait(false)
            : Array.Empty<ContainerAuditTransaction>();

        return await CreatePreviewJobAsync(
            worldId,
            requestedBy,
            string.IsNullOrWhiteSpace(filterSummary) ? $"{ToLabel(operation)} preview" : filterSummary,
            operation,
            selectedEvents,
            selectedTransactions,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<RollbackJob> CreatePreviewJobAsync(
        string worldId,
        AuditActor requestedBy,
        string filterSummary,
        RollbackJobOperation operation,
        IReadOnlyList<BlockAuditEvent> selectedEvents,
        IReadOnlyList<ContainerAuditTransaction> selectedTransactions,
        CancellationToken cancellationToken)
    {
        var orderedEntries = Order(selectedEvents, selectedTransactions, operation);
        var plannedBlockCount = 0;
        var plannedContainerCount = 0;
        var chunkSet = new HashSet<ChunkPosition>();
        DateTimeOffset? oldestTargetOccurredAt = null;
        var persistedEntries = new RollbackJobPlanEntryCreate[orderedEntries.Length];

        for (var index = 0; index < orderedEntries.Length; index++)
        {
            var entry = orderedEntries[index];
            persistedEntries[index] = new RollbackJobPlanEntryCreate(entry.TargetType, entry.SourceRecordId, index);

            if (entry.TargetType == RollbackJobEntryTargetType.Block)
            {
                plannedBlockCount++;
                chunkSet.Add(entry.Position.ToChunkPosition(_options.ChunkSize));
            }
            else
            {
                plannedContainerCount++;
            }

            if (!oldestTargetOccurredAt.HasValue || entry.OccurredAt < oldestTargetOccurredAt.Value)
            {
                oldestTargetOccurredAt = entry.OccurredAt;
            }
        }

        var request = new RollbackJobCreateRequest(
            worldId,
            requestedBy,
            operation,
            filterSummary,
            RollbackJobState.Previewed,
            plannedBlockCount,
            plannedContainerCount,
            chunkSet.Count,
            oldestTargetOccurredAt);

        return await _repository.CreateRollbackJobAsync(
            request,
            persistedEntries,
            cancellationToken).ConfigureAwait(false);
    }

    private static RollbackJobOperation Invert(RollbackJobOperation operation)
    {
        return operation == RollbackJobOperation.Rollback
            ? RollbackJobOperation.Restore
            : RollbackJobOperation.Rollback;
    }

    private RollbackSourceEntry[] Order(
        IReadOnlyList<BlockAuditEvent> blockEvents,
        IReadOnlyList<ContainerAuditTransaction> containerTransactions,
        RollbackJobOperation operation)
    {
        var results = new List<RollbackSourceEntry>(blockEvents.Count + containerTransactions.Count);

        for (var index = 0; index < blockEvents.Count; index++)
        {
            var entry = blockEvents[index];
            if (!entry.Id.HasValue)
            {
                continue;
            }

            results.Add(new RollbackSourceEntry(RollbackJobEntryTargetType.Block, entry.Id.Value, entry.Position, entry.OccurredAt));
        }

        for (var index = 0; index < containerTransactions.Count; index++)
        {
            var entry = containerTransactions[index];
            if (!entry.Id.HasValue)
            {
                continue;
            }

            results.Add(new RollbackSourceEntry(RollbackJobEntryTargetType.Container, entry.Id.Value, entry.Position, entry.OccurredAt));
        }

        results.Sort((left, right) => CompareEntries(left, right, operation, _options.ChunkSize));
        return results.ToArray();
    }

    private static string ToLabel(RollbackJobOperation operation)
    {
        return operation == RollbackJobOperation.Rollback ? "rollback" : "restore";
    }

    private static int CompareEntries(
        RollbackSourceEntry left,
        RollbackSourceEntry right,
        RollbackJobOperation operation,
        int chunkSize)
    {
        var occurredComparison = operation == RollbackJobOperation.Rollback
            ? right.OccurredAt.CompareTo(left.OccurredAt)
            : left.OccurredAt.CompareTo(right.OccurredAt);
        if (occurredComparison != 0)
        {
            return occurredComparison;
        }

        if (left.TargetType == right.TargetType && left.TargetType == RollbackJobEntryTargetType.Block)
        {
            var leftChunk = left.Position.ToChunkPosition(chunkSize);
            var rightChunk = right.Position.ToChunkPosition(chunkSize);
            var chunkX = leftChunk.X.CompareTo(rightChunk.X);
            if (chunkX != 0)
            {
                return chunkX;
            }

            var chunkZ = leftChunk.Z.CompareTo(rightChunk.Z);
            if (chunkZ != 0)
            {
                return chunkZ;
            }

            var chunkY = leftChunk.Y.CompareTo(rightChunk.Y);
            if (chunkY != 0)
            {
                return chunkY;
            }
        }

        var targetTypeComparison = left.TargetType.CompareTo(right.TargetType);
        if (targetTypeComparison != 0)
        {
            return targetTypeComparison;
        }

        return operation == RollbackJobOperation.Rollback
            ? right.SourceRecordId.CompareTo(left.SourceRecordId)
            : left.SourceRecordId.CompareTo(right.SourceRecordId);
    }

    private sealed record RollbackSourceEntry(
        RollbackJobEntryTargetType TargetType,
        long SourceRecordId,
        BlockPosition Position,
        DateTimeOffset OccurredAt);
}
