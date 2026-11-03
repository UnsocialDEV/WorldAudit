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
        ValidateWorld(job.WorldId);

        if (job.State is RollbackJobState.Completed or RollbackJobState.CompletedWithConflicts or RollbackJobState.Failed or RollbackJobState.Cancelled)
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
        if (plannedEntries.Any(entry => !string.Equals(entry.WorldId, job.WorldId, StringComparison.Ordinal)))
            throw new InvalidOperationException("The job contains entries from another world.");
        if (plannedEntries.Count == 0)
        {
            await _repository.UpdateRollbackJobStateAsync(
                jobId,
                HasConflictsOrFailures(job) ? RollbackJobState.CompletedWithConflicts : RollbackJobState.Completed,
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

    public async Task<RollbackJob?> CancelAsync(long jobId, CancellationToken cancellationToken = default)
    {
        var job = await _repository.GetRollbackJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null) return null;
        ValidateWorld(job.WorldId);
        if (job.State is not (RollbackJobState.Previewed or RollbackJobState.Paused))
        {
            throw new InvalidOperationException("Only previewed or paused jobs can be cancelled.");
        }
        await _repository.UpdateRollbackJobStateAsync(jobId, RollbackJobState.Cancelled,
            completedAt: DateTimeOffset.UtcNow, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await _repository.GetRollbackJobAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    public bool HasActiveJobs()
    {
        return GetActiveJobCount() > 0;
    }

    private void ValidateWorld(string worldId)
    {
        if (!string.IsNullOrWhiteSpace(_options.WorldId) && !string.Equals(_options.WorldId, worldId, StringComparison.Ordinal))
            throw new InvalidOperationException("This job belongs to another world.");
    }

    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        QueuedRollbackJob[] jobs;
        lock (_sync)
        {
            jobs = _activeJob is null ? _queuedJobs.ToArray() : new[] { _activeJob }.Concat(_queuedJobs).ToArray();
        }
        foreach (var job in jobs)
        {
            await PersistUpdatesAsync(job, cancellationToken).ConfigureAwait(false);
            await _repository.UpdateRollbackJobStateAsync(job.JobId, RollbackJobState.Paused,
                lastError: "Server stopped. Use /wa apply to resume or /wa cancel to discard remaining entries.",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        lock (_sync)
        {
            _activeJob = null;
            _queuedJobs.Clear();
            _queuedJobIds.Clear();
        }
    }

    public async Task ProcessTickAsync(CancellationToken cancellationToken = default)
    {
        QueuedRollbackJob? jobToRun;

        lock (_sync)
        {
            if (_activeJob is null && _queuedJobs.Count > 0)
            {
                _activeJob = _queuedJobs.Dequeue();
                _queuedJobIds.Remove(_activeJob.JobId);
            }

            jobToRun = _activeJob;
        }

        if (jobToRun is null)
        {
            return;
        }

        if (!jobToRun.RunningStatePersisted)
        {
            await _repository.UpdateRollbackJobStateAsync(
                jobToRun.JobId,
                RollbackJobState.Running,
                startedAt: DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            jobToRun.RunningStatePersisted = true;
        }

        if (jobToRun.UnpersistedUpdates.Count > 0)
        {
            await PersistUpdatesAsync(jobToRun, cancellationToken).ConfigureAwait(false);
        }

        var batch = BuildNextBatch(jobToRun);
        if (batch.Count == 0)
        {
            await CompleteActiveJobAsync(jobToRun.JobId, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var entry in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var mutationScope = _mutationGuard.Begin(entry.WorldId, entry.Position);
                switch (entry)
                {
                    case RollbackBlockPlanEntry blockEntry:
                        jobToRun.UnpersistedUpdates.Add(await ApplyBlockEntryAsync(blockEntry, CancellationToken.None).ConfigureAwait(false));
                        break;
                    case RollbackContainerPlanEntry containerEntry:
                        jobToRun.UnpersistedUpdates.Add(await ApplyContainerEntryAsync(containerEntry, CancellationToken.None).ConfigureAwait(false));
                        break;
                    default:
                        jobToRun.UnpersistedUpdates.Add(new RollbackBatchEntryUpdate(
                            entry.JobEntryId,
                            entry.TargetType,
                            RollbackJobEntryResult.Failed,
                            $"Unsupported rollback entry target '{entry.TargetType}'."));
                        break;
                }
            }
            catch (Exception exception)
            {
                jobToRun.UnpersistedUpdates.Add(new RollbackBatchEntryUpdate(entry.JobEntryId, entry.TargetType, RollbackJobEntryResult.Failed, exception.Message));
            }
            lock (_sync) jobToRun.PendingEntries.Dequeue();
        }

        await PersistUpdatesAsync(jobToRun, cancellationToken).ConfigureAwait(false);
        if (jobToRun.PendingEntries.Count == 0)
            await CompleteActiveJobAsync(jobToRun.JobId, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistUpdatesAsync(QueuedRollbackJob job, CancellationToken cancellationToken)
    {
        // A failed write must be retried without applying the world mutations a second time.
        await _repository.RecordRollbackBatchAsync(job.JobId, job.UnpersistedUpdates, cancellationToken).ConfigureAwait(false);
        job.UnpersistedUpdates.Clear();
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

            batch.Add(jobToRun.PendingEntries.Peek());
            if (batch[0] is not RollbackBlockPlanEntry firstBlockEntry)
            {
                return batch;
            }

            foreach (var candidate in jobToRun.PendingEntries.Skip(1))
            {
                if (batch.Count >= _options.RollbackMaxBlocksPerTick
                    || candidate is not RollbackBlockPlanEntry nextBlockEntry
                    || !nextBlockEntry.Chunk.Equals(firstBlockEntry.Chunk)) break;
                batch.Add(candidate);
            }
        }

        return batch;
    }

    private async Task<RollbackBatchEntryUpdate> ApplyBlockEntryAsync(
        RollbackBlockPlanEntry entry,
        CancellationToken cancellationToken)
    {
        var applyResult = await _blockApplier.ApplyAsync(entry, cancellationToken).ConfigureAwait(false);
        BlockAuditEvent? mutation = null;
        if (applyResult.ChangedWorld)
        {
            mutation = new BlockAuditEvent(
                Id: null, WorldId: entry.WorldId, Position: entry.Position, OccurredAt: DateTimeOffset.UtcNow,
                Actor: entry.Operation == RollbackJobOperation.Rollback ? RollbackActor : RestoreActor,
                Cause: entry.Operation == RollbackJobOperation.Rollback ? AuditCause.Rollback : AuditCause.Restore,
                Action: BlockAuditAction.Restore,
                OldBlockCode: applyResult.PreviousBlockCode,
                NewBlockCode: applyResult.ActualBlockCode,
                OldBlockEntitySnapshot: applyResult.PreviousBlockEntitySnapshot,
                NewBlockEntitySnapshot: applyResult.ActualBlockEntitySnapshot,
                SourceJobId: entry.JobId, SourceEventId: entry.SourceEventId, Layer: entry.Layer);
        }
        var result = applyResult.Status switch
        {
            RollbackBlockApplyStatus.Applied => RollbackJobEntryResult.Applied,
            RollbackBlockApplyStatus.Conflict => RollbackJobEntryResult.Conflict,
            _ => RollbackJobEntryResult.Failed
        };
        return new RollbackBatchEntryUpdate(entry.JobEntryId, entry.TargetType, result, applyResult.ErrorText, BlockMutation: mutation);
    }

    private async Task<RollbackBatchEntryUpdate> ApplyContainerEntryAsync(
        RollbackContainerPlanEntry entry,
        CancellationToken cancellationToken)
    {
        var applyResult = await _containerApplier.ApplyAsync(entry, cancellationToken).ConfigureAwait(false);
        ContainerAuditTransaction? mutation = null;
        if (applyResult.ChangedWorld)
        {
            mutation = new ContainerAuditTransaction(
                Id: null, WorldId: entry.WorldId, Position: entry.Position, OccurredAt: DateTimeOffset.UtcNow,
                Actor: entry.Operation == RollbackJobOperation.Rollback ? RollbackActor : RestoreActor,
                Cause: entry.Operation == RollbackJobOperation.Rollback ? AuditCause.Rollback : AuditCause.Restore,
                InventoryType: entry.InventoryType, ContainerLabel: entry.ContainerLabel,
                BeforeSnapshot: applyResult.PreviousSnapshot,
                AfterSnapshot: applyResult.ActualSnapshot,
                Lines: BuildContainerLines(applyResult.PreviousSnapshot, applyResult.ActualSnapshot),
                SourceJobId: entry.JobId);
        }
        var result = applyResult.Status switch
        {
            RollbackContainerApplyStatus.Applied => RollbackJobEntryResult.Applied,
            RollbackContainerApplyStatus.Conflict => RollbackJobEntryResult.Conflict,
            _ => RollbackJobEntryResult.Failed
        };
        return new RollbackBatchEntryUpdate(entry.JobEntryId, entry.TargetType, result, applyResult.ErrorText, ContainerMutation: mutation);
    }

    private async Task CompleteActiveJobAsync(long jobId, CancellationToken cancellationToken)
    {
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
        lock (_sync)
        {
            if (_activeJob?.JobId == jobId)
            {
                _activeJob = null;
            }
        }


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

            if (!string.IsNullOrWhiteSpace(beforeSlot.ItemCode) && !string.IsNullOrWhiteSpace(afterSlot.ItemCode)
                && !string.Equals(beforeSlot.ItemCode, afterSlot.ItemCode, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(new ContainerAuditLine(null, beforeSlot.ItemCode, slotId, -beforeSlot.Quantity,
                    beforeSlot.Quantity, 0, beforeSlot.StackBytes, null));
                lines.Add(new ContainerAuditLine(null, afterSlot.ItemCode, slotId, afterSlot.Quantity,
                    0, afterSlot.Quantity, null, afterSlot.StackBytes));
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

    private sealed class QueuedRollbackJob
    {
        public QueuedRollbackJob(long jobId, Queue<RollbackPlanEntry> pendingEntries)
        {
            JobId = jobId;
            PendingEntries = pendingEntries;
        }

        public long JobId { get; }

        public Queue<RollbackPlanEntry> PendingEntries { get; }

        public List<RollbackBatchEntryUpdate> UnpersistedUpdates { get; } = [];

        public bool RunningStatePersisted { get; set; }
    }

    private sealed record SnapshotSlot(int SlotId, string? ItemCode, int Quantity, byte[]? StackBytes);
}
