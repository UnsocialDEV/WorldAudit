using System.IO;
using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Domain;

namespace WorldAudit.Application.Services;

public sealed class RollbackExecutionCoordinator
{
    private static readonly AuditActor RollbackActor = new("#rollback", null, true);
    private static readonly AuditActor RestoreActor = new("#restore", null, true);

    private readonly IAuditRepository _repository;
    private readonly IAuditWriter _auditWriter;
    private readonly IRollbackBlockApplier _blockApplier;
    private readonly IRollbackContainerApplier _containerApplier;
    private readonly IWorldMutationGuard _mutationGuard;
    private readonly WorldAuditOptions _options;
    private readonly object _sync = new();
    private readonly Queue<QueuedRollbackJob> _queuedJobs = new();
    private readonly HashSet<long> _queuedJobIds = [];
    private QueuedRollbackJob? _activeJob;

    public RollbackExecutionCoordinator(
        IAuditRepository repository,
        IAuditWriter auditWriter,
        IRollbackBlockApplier blockApplier,
        IRollbackContainerApplier containerApplier,
        IWorldMutationGuard mutationGuard,
        WorldAuditOptions options)
    {
        _repository = repository;
        _auditWriter = auditWriter;
        _blockApplier = blockApplier;
        _containerApplier = containerApplier;
        _mutationGuard = mutationGuard;
        _options = options;
    }

    public async Task<RollbackJob?> QueueApplyAsync(long jobId, CancellationToken cancellationToken = default)
    {
        var job = await _repository.GetRollbackJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return null;
        }

        if (job.State is RollbackJobState.Completed or RollbackJobState.CompletedWithConflicts or RollbackJobState.Failed)
        {
            return job;
        }

        lock (_sync)
        {
            if (_activeJob?.JobId == jobId || _queuedJobIds.Contains(jobId))
            {
                return job;
            }
        }

        var plannedEntries = await _repository.GetRollbackPlanEntriesAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (plannedEntries.Count == 0)
        {
            await _repository.UpdateRollbackJobStateAsync(
                jobId,
                RollbackJobState.Completed,
                completedAt: DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return await _repository.GetRollbackJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        }

        var queuedJob = new QueuedRollbackJob(jobId, new Queue<RollbackPlanEntry>(plannedEntries));

        lock (_sync)
        {
            if (_activeJob?.JobId == jobId || _queuedJobIds.Contains(jobId))
            {
                return job;
            }

            _queuedJobs.Enqueue(queuedJob);
            _queuedJobIds.Add(jobId);
        }

        await _repository.UpdateRollbackJobStateAsync(
            jobId,
            RollbackJobState.Queued,
            queuedAt: DateTimeOffset.UtcNow,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return await _repository.GetRollbackJobAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    public int GetActiveJobCount()
    {
        lock (_sync)
        {
            return (_activeJob is null ? 0 : 1) + _queuedJobs.Count;
        }
    }

    public bool HasActiveJobs()
    {
        return GetActiveJobCount() > 0;
    }

    public async Task ProcessTickAsync(CancellationToken cancellationToken = default)
    {
        QueuedRollbackJob? jobToRun;
        var transitionedToRunning = false;

        lock (_sync)
        {
            if (_activeJob is null && _queuedJobs.Count > 0)
            {
                _activeJob = _queuedJobs.Dequeue();
                _queuedJobIds.Remove(_activeJob.JobId);
                transitionedToRunning = true;
            }

            jobToRun = _activeJob;
        }

        if (jobToRun is null)
        {
            return;
        }

        if (transitionedToRunning)
        {
            await _repository.UpdateRollbackJobStateAsync(
                jobToRun.JobId,
                RollbackJobState.Running,
                startedAt: DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var batch = BuildNextBatch(jobToRun);
        if (batch.Count == 0)
        {
            await CompleteActiveJobAsync(jobToRun.JobId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var updates = new List<RollbackBatchEntryUpdate>(batch.Count);
        foreach (var entry in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var mutationScope = _mutationGuard.Begin(entry.WorldId, entry.Position);
                switch (entry)
                {
                    case RollbackBlockPlanEntry blockEntry:
                        updates.Add(await ApplyBlockEntryAsync(blockEntry, cancellationToken).ConfigureAwait(false));
                        break;
                    case RollbackContainerPlanEntry containerEntry:
                        updates.Add(await ApplyContainerEntryAsync(containerEntry, cancellationToken).ConfigureAwait(false));
                        break;
                    default:
                        updates.Add(new RollbackBatchEntryUpdate(
                            entry.JobEntryId,
                            entry.TargetType,
                            RollbackJobEntryResult.Failed,
                            $"Unsupported rollback entry target '{entry.TargetType}'."));
                        break;
                }
            }
            catch (Exception exception)
            {
                updates.Add(new RollbackBatchEntryUpdate(entry.JobEntryId, entry.TargetType, RollbackJobEntryResult.Failed, exception.Message));
            }
        }

        await _repository.RecordRollbackBatchAsync(jobToRun.JobId, updates, cancellationToken).ConfigureAwait(false);

        var shouldComplete = false;
        lock (_sync)
        {
            if (_activeJob?.JobId == jobToRun.JobId && jobToRun.PendingEntries.Count == 0)
            {
                shouldComplete = true;
                _activeJob = null;
            }
        }

        if (shouldComplete)
        {
            var updatedJob = await _repository.GetRollbackJobAsync(jobToRun.JobId, cancellationToken).ConfigureAwait(false);
            var finalState = updatedJob is not null && HasConflictsOrFailures(updatedJob)
                ? RollbackJobState.CompletedWithConflicts
                : RollbackJobState.Completed;

            await _repository.UpdateRollbackJobStateAsync(
                jobToRun.JobId,
                finalState,
                completedAt: DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await _auditWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private List<RollbackPlanEntry> BuildNextBatch(QueuedRollbackJob jobToRun)
    {
        var batch = new List<RollbackPlanEntry>(_options.RollbackMaxBlocksPerTick);
        lock (_sync)
        {
            if (_activeJob?.JobId != jobToRun.JobId || jobToRun.PendingEntries.Count == 0)
            {
                return batch;
            }

            batch.Add(jobToRun.PendingEntries.Dequeue());
            if (batch[0] is not RollbackBlockPlanEntry firstBlockEntry)
            {
                return batch;
            }

            while (_activeJob?.JobId == jobToRun.JobId
                && batch.Count < _options.RollbackMaxBlocksPerTick
                && jobToRun.PendingEntries.Count > 0
                && jobToRun.PendingEntries.Peek() is RollbackBlockPlanEntry nextBlockEntry
                && nextBlockEntry.Chunk.Equals(firstBlockEntry.Chunk))
            {
                batch.Add(jobToRun.PendingEntries.Dequeue());
            }
        }

        return batch;
    }

    private async Task<RollbackBatchEntryUpdate> ApplyBlockEntryAsync(
        RollbackBlockPlanEntry entry,
        CancellationToken cancellationToken)
    {
        var applyResult = await _blockApplier.ApplyAsync(entry, cancellationToken).ConfigureAwait(false);
        if (applyResult.Status == RollbackBlockApplyStatus.Failed)
        {
            return new RollbackBatchEntryUpdate(
                entry.JobEntryId,
                RollbackJobEntryTargetType.Block,
                RollbackJobEntryResult.Failed,
                applyResult.ErrorText ?? $"Unknown {DescribeOperation(entry.Operation)} apply failure.");
        }

        if (applyResult.Status == RollbackBlockApplyStatus.Conflict)
        {
            return new RollbackBatchEntryUpdate(
                entry.JobEntryId,
                RollbackJobEntryTargetType.Block,
                RollbackJobEntryResult.Conflict,
                applyResult.ErrorText ?? $"{ToTitleCase(entry.Operation)} apply conflict.");
        }

        if (applyResult.ChangedWorld)
        {
            var auditCause = entry.Operation == RollbackJobOperation.Rollback ? AuditCause.Rollback : AuditCause.Restore;
            var actor = entry.Operation == RollbackJobOperation.Rollback ? RollbackActor : RestoreActor;
            await _auditWriter.QueueBlockEventAsync(
                new BlockAuditEvent(
                    Id: null,
                    WorldId: entry.WorldId,
                    Position: entry.Position,
                    OccurredAt: DateTimeOffset.UtcNow,
                    Actor: actor,
                    Cause: auditCause,
                    Action: BlockAuditAction.Restore,
                    OldBlockCode: applyResult.PreviousBlockCode,
                    NewBlockCode: entry.TargetBlockCode,
                    OldBlockEntitySnapshot: null,
                    NewBlockEntitySnapshot: entry.TargetBlockEntitySnapshot,
                    SourceJobId: entry.JobId,
                    SourceEventId: entry.SourceEventId),
                cancellationToken).ConfigureAwait(false);
        }

        return new RollbackBatchEntryUpdate(entry.JobEntryId, RollbackJobEntryTargetType.Block, RollbackJobEntryResult.Applied);
    }

    private async Task<RollbackBatchEntryUpdate> ApplyContainerEntryAsync(
        RollbackContainerPlanEntry entry,
        CancellationToken cancellationToken)
    {
        var applyResult = await _containerApplier.ApplyAsync(entry, cancellationToken).ConfigureAwait(false);
        if (applyResult.Status == RollbackContainerApplyStatus.Failed)
        {
            return new RollbackBatchEntryUpdate(
                entry.JobEntryId,
                RollbackJobEntryTargetType.Container,
                RollbackJobEntryResult.Failed,
                applyResult.ErrorText ?? $"Unknown {DescribeOperation(entry.Operation)} container apply failure.");
        }

        if (applyResult.Status == RollbackContainerApplyStatus.Conflict)
        {
            return new RollbackBatchEntryUpdate(
                entry.JobEntryId,
                RollbackJobEntryTargetType.Container,
                RollbackJobEntryResult.Conflict,
                applyResult.ErrorText ?? $"{ToTitleCase(entry.Operation)} container apply conflict.");
        }

        if (applyResult.ChangedWorld)
        {
            var auditCause = entry.Operation == RollbackJobOperation.Rollback ? AuditCause.Rollback : AuditCause.Restore;
            var actor = entry.Operation == RollbackJobOperation.Rollback ? RollbackActor : RestoreActor;
            await _auditWriter.QueueContainerTransactionAsync(
                new ContainerAuditTransaction(
                    Id: null,
                    WorldId: entry.WorldId,
                    Position: entry.Position,
                    OccurredAt: DateTimeOffset.UtcNow,
                    Actor: actor,
                    Cause: auditCause,
                    InventoryType: entry.InventoryType,
                    ContainerLabel: entry.ContainerLabel,
                    BeforeSnapshot: applyResult.PreviousSnapshot,
                    AfterSnapshot: entry.TargetSnapshot,
                    Lines: BuildContainerLines(applyResult.PreviousSnapshot, entry.TargetSnapshot),
                    SourceJobId: entry.JobId),
                cancellationToken).ConfigureAwait(false);
        }

        return new RollbackBatchEntryUpdate(entry.JobEntryId, RollbackJobEntryTargetType.Container, RollbackJobEntryResult.Applied);
    }

    private async Task CompleteActiveJobAsync(long jobId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_activeJob?.JobId == jobId)
            {
                _activeJob = null;
            }
        }

        var job = await _repository.GetRollbackJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        var finalState = job is not null && HasConflictsOrFailures(job)
            ? RollbackJobState.CompletedWithConflicts
            : RollbackJobState.Completed;

        await _repository.UpdateRollbackJobStateAsync(
            jobId,
            finalState,
            completedAt: DateTimeOffset.UtcNow,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await _auditWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool HasConflictsOrFailures(RollbackJob job)
    {
        return job.ConflictBlockCount > 0
            || job.ConflictContainerCount > 0
            || job.FailedBlockCount > 0
            || job.FailedContainerCount > 0;
    }

    private static IReadOnlyList<ContainerAuditLine> BuildContainerLines(byte[]? previousSnapshot, byte[]? targetSnapshot)
    {
        var before = ReadSnapshot(previousSnapshot);
        var after = ReadSnapshot(targetSnapshot);
        var count = Math.Max(before.Count, after.Count);
        var lines = new List<ContainerAuditLine>(count);

        for (var slotId = 0; slotId < count; slotId++)
        {
            var beforeSlot = before.TryGetValue(slotId, out var beforeValue)
                ? beforeValue
                : new SnapshotSlot(slotId, null, 0, null);
            var afterSlot = after.TryGetValue(slotId, out var afterValue)
                ? afterValue
                : new SnapshotSlot(slotId, null, 0, null);

            if (SnapshotsEqual(beforeSlot, afterSlot))
            {
                continue;
            }

            var itemCode = afterSlot.ItemCode ?? beforeSlot.ItemCode;
            if (string.IsNullOrWhiteSpace(itemCode))
            {
                continue;
            }

            lines.Add(new ContainerAuditLine(
                Id: null,
                ItemCode: itemCode,
                SlotId: slotId,
                QuantityDelta: afterSlot.Quantity - beforeSlot.Quantity,
                BeforeQuantity: beforeSlot.Quantity,
                AfterQuantity: afterSlot.Quantity,
                StackBeforeSnapshot: beforeSlot.StackBytes,
                StackAfterSnapshot: afterSlot.StackBytes));
        }

        return lines;
    }

    private static Dictionary<int, SnapshotSlot> ReadSnapshot(byte[]? snapshotBytes)
    {
        var result = new Dictionary<int, SnapshotSlot>();
        if (snapshotBytes is null || snapshotBytes.Length == 0)
        {
            return result;
        }

        using var stream = new MemoryStream(snapshotBytes, writable: false);
        using var reader = new BinaryReader(stream);
        var count = reader.ReadInt32();
        for (var index = 0; index < count; index++)
        {
            var slotId = reader.ReadInt32();
            var itemCode = reader.ReadString();
            var quantity = reader.ReadInt32();
            var stackLength = reader.ReadInt32();
            var stackBytes = stackLength > 0 ? reader.ReadBytes(stackLength) : null;
            result[slotId] = new SnapshotSlot(slotId, string.IsNullOrWhiteSpace(itemCode) ? null : itemCode, quantity, stackBytes);
        }

        return result;
    }

    private static bool SnapshotsEqual(SnapshotSlot before, SnapshotSlot after)
    {
        if (!string.Equals(before.ItemCode, after.ItemCode, StringComparison.OrdinalIgnoreCase)
            || before.Quantity != after.Quantity)
        {
            return false;
        }

        if (before.StackBytes is null || after.StackBytes is null)
        {
            return before.StackBytes is null && after.StackBytes is null;
        }

        if (before.StackBytes.Length != after.StackBytes.Length)
        {
            return false;
        }

        for (var index = 0; index < before.StackBytes.Length; index++)
        {
            if (before.StackBytes[index] != after.StackBytes[index])
            {
                return false;
            }
        }

        return true;
    }

    private static string DescribeOperation(RollbackJobOperation operation)
    {
        return operation == RollbackJobOperation.Rollback ? "rollback" : "restore";
    }

    private static string ToTitleCase(RollbackJobOperation operation)
    {
        return operation == RollbackJobOperation.Rollback ? "Rollback" : "Restore";
    }

    private sealed class QueuedRollbackJob
    {
        public QueuedRollbackJob(long jobId, Queue<RollbackPlanEntry> pendingEntries)
        {
            JobId = jobId;
            PendingEntries = pendingEntries;
        }

        public long JobId { get; }

        public Queue<RollbackPlanEntry> PendingEntries { get; }
    }

    private sealed record SnapshotSlot(int SlotId, string? ItemCode, int Quantity, byte[]? StackBytes);
}
