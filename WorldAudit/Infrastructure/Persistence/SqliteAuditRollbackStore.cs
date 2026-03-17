using Microsoft.Data.Sqlite;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal sealed class SqliteAuditRollbackStore
{
    private readonly SqliteAuditRepositoryState _state;
    private readonly SqliteAuditLookupResolver _resolver;
    private readonly SqliteAuditRecordReader _reader;

    public SqliteAuditRollbackStore(
        SqliteAuditRepositoryState state,
        SqliteAuditLookupResolver resolver,
        SqliteAuditRecordReader reader)
    {
        _state = state;
        _resolver = resolver;
        _reader = reader;
    }

    public async Task<RollbackJob> CreateRollbackJobAsync(
        RollbackJobCreateRequest request,
        IReadOnlyList<RollbackJobPlanEntryCreate> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entries);

        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var worldId = await _resolver.ResolveWorldIdAsync(connection, transaction, request.WorldId, cancellationToken).ConfigureAwait(false);
        var requestedByActorId = await _resolver.ResolveActorIdAsync(connection, transaction, request.RequestedBy, cancellationToken).ConfigureAwait(false);

        long jobId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO rollback_jobs (
                    world_id, requested_by_actor_id, operation_code, filter_summary, state_code, created_at_ms, planned_block_count,
                    planned_container_count, planned_chunk_count, applied_block_count, applied_container_count, conflict_block_count,
                    conflict_container_count, failed_block_count, failed_container_count, oldest_target_occurred_at_ms, last_error)
                VALUES (
                    $worldId, $requestedByActorId, $operationCode, $filterSummary, $stateCode, $createdAt, $plannedBlockCount,
                    $plannedContainerCount, $plannedChunkCount, 0, 0, 0, 0, 0, 0, $oldestTargetOccurredAt, NULL);
                SELECT last_insert_rowid();
                """;

            command.Parameters.AddWithValue("$worldId", worldId);
            command.Parameters.AddWithValue("$requestedByActorId", requestedByActorId);
            command.Parameters.AddWithValue("$operationCode", SqliteAuditValueCodec.ToCode(request.Operation));
            command.Parameters.AddWithValue("$filterSummary", request.FilterSummary);
            command.Parameters.AddWithValue("$stateCode", SqliteAuditValueCodec.ToCode(request.State));
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$plannedBlockCount", request.PlannedBlockCount);
            command.Parameters.AddWithValue("$plannedContainerCount", request.PlannedContainerCount);
            command.Parameters.AddWithValue("$plannedChunkCount", request.PlannedChunkCount);
            command.Parameters.AddWithValue("$oldestTargetOccurredAt", request.OldestTargetOccurredAt.HasValue ? request.OldestTargetOccurredAt.Value.ToUnixTimeMilliseconds() : DBNull.Value);

            jobId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            await using var entryCommand = connection.CreateCommand();
            entryCommand.Transaction = transaction;
            entryCommand.CommandText =
                """
                INSERT INTO rollback_job_entries (
                    job_id, target_type_code, target_event_id, execution_order, result_code, applied_at_ms, error_text)
                VALUES (
                    $jobId, $targetTypeCode, $targetEventId, $executionOrder, 'planned', NULL, NULL);
                """;
            entryCommand.Parameters.AddWithValue("$jobId", jobId);
            entryCommand.Parameters.AddWithValue("$targetTypeCode", SqliteAuditValueCodec.ToCode(entry.TargetType));
            entryCommand.Parameters.AddWithValue("$targetEventId", entry.SourceRecordId);
            entryCommand.Parameters.AddWithValue("$executionOrder", entry.ExecutionOrder);
            await entryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (await GetRollbackJobAsync(jobId, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<RollbackJob?> GetRollbackJobAsync(long jobId, CancellationToken cancellationToken)
    {
        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SqliteAuditSql.BaseRollbackJobSelectSql}WHERE rj.id = $jobId LIMIT 1;";
        command.Parameters.AddWithValue("$jobId", jobId);

        var jobs = await _reader.ReadRollbackJobsAsync(command, cancellationToken).ConfigureAwait(false);
        return jobs.Count == 0 ? null : jobs[0];
    }

    public async Task<IReadOnlyList<RollbackJob>> GetRollbackJobsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SqliteAuditSql.BaseRollbackJobSelectSql}ORDER BY rj.id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        return await _reader.ReadRollbackJobsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BlockAuditEvent>> GetRollbackJobSourceBlockEventsAsync(
        long jobId,
        bool appliedOnly,
        CancellationToken cancellationToken)
    {
        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            {SqliteAuditSql.BaseBlockSelectSql}
            INNER JOIN rollback_job_entries rje ON rje.target_event_id = be.id
            WHERE rje.job_id = $jobId
              AND rje.target_type_code = 'block'
              {(appliedOnly ? "AND rje.result_code = 'applied'" : string.Empty)}
            ORDER BY rje.execution_order;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        return await _reader.ReadBlockEventsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContainerAuditTransaction>> GetRollbackJobSourceContainerTransactionsAsync(
        long jobId,
        bool appliedOnly,
        CancellationToken cancellationToken)
    {
        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            {SqliteAuditSql.BaseContainerSelectSql}
            INNER JOIN rollback_job_entries rje ON rje.target_event_id = ct.id
            WHERE rje.job_id = $jobId
              AND rje.target_type_code = 'container'
              {(appliedOnly ? "AND rje.result_code = 'applied'" : string.Empty)}
            ORDER BY rje.execution_order;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        return await _reader.ReadContainerTransactionsAsync(connection, command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RollbackPlanEntry>> GetRollbackPlanEntriesAsync(long jobId, CancellationToken cancellationToken)
    {
        var blockEntries = await GetRollbackBlockPlanEntriesAsync(jobId, cancellationToken).ConfigureAwait(false);
        var containerEntries = await GetRollbackContainerPlanEntriesAsync(jobId, cancellationToken).ConfigureAwait(false);

        if (blockEntries.Count == 0)
        {
            return containerEntries;
        }

        if (containerEntries.Count == 0)
        {
            return blockEntries;
        }

        var results = new List<RollbackPlanEntry>(blockEntries.Count + containerEntries.Count);
        var blockIndex = 0;
        var containerIndex = 0;
        while (blockIndex < blockEntries.Count || containerIndex < containerEntries.Count)
        {
            if (containerIndex >= containerEntries.Count
                || (blockIndex < blockEntries.Count && blockEntries[blockIndex].ExecutionOrder <= containerEntries[containerIndex].ExecutionOrder))
            {
                results.Add(blockEntries[blockIndex++]);
            }
            else
            {
                results.Add(containerEntries[containerIndex++]);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<RollbackBlockPlanEntry>> GetRollbackBlockPlanEntriesAsync(long jobId, CancellationToken cancellationToken)
    {
        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                rje.id AS job_entry_id,
                rje.job_id,
                rje.execution_order,
                rj.operation_code,
                be.id AS source_event_id,
                w.code AS world_code,
                be.x,
                be.y,
                be.z,
                be.chunk_x,
                be.chunk_y,
                be.chunk_z,
                be.occurred_at_ms,
                oldb.code AS old_block_code,
                newb.code AS new_block_code,
                be.old_block_entity_blob,
                be.new_block_entity_blob
            FROM rollback_job_entries rje
            INNER JOIN rollback_jobs rj ON rj.id = rje.job_id
            INNER JOIN block_events be ON be.id = rje.target_event_id
            INNER JOIN worlds w ON w.id = be.world_id
            LEFT JOIN blocks oldb ON oldb.id = be.old_block_id
            LEFT JOIN blocks newb ON newb.id = be.new_block_id
            WHERE rje.job_id = $jobId
              AND rje.target_type_code = 'block'
              AND rje.result_code = 'planned'
            ORDER BY rje.execution_order;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);

        var results = new List<RollbackBlockPlanEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var operation = SqliteAuditValueCodec.ParseRollbackJobOperation(reader.GetString(reader.GetOrdinal("operation_code")));
            results.Add(new RollbackBlockPlanEntry(
                JobEntryId: reader.GetInt64(reader.GetOrdinal("job_entry_id")),
                JobId: reader.GetInt64(reader.GetOrdinal("job_id")),
                SourceEventId: reader.GetInt64(reader.GetOrdinal("source_event_id")),
                WorldId: reader.GetString(reader.GetOrdinal("world_code")),
                Operation: operation,
                Position: new BlockPosition(
                    reader.GetInt32(reader.GetOrdinal("x")),
                    reader.GetInt32(reader.GetOrdinal("y")),
                    reader.GetInt32(reader.GetOrdinal("z"))),
                Chunk: new ChunkPosition(
                    reader.GetInt32(reader.GetOrdinal("chunk_x")),
                    reader.GetInt32(reader.GetOrdinal("chunk_y")),
                    reader.GetInt32(reader.GetOrdinal("chunk_z"))),
                SourceOccurredAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("occurred_at_ms"))),
                TargetBlockCode: operation == RollbackJobOperation.Rollback
                    ? (reader.IsDBNull(reader.GetOrdinal("old_block_code")) ? null : reader.GetString(reader.GetOrdinal("old_block_code")))
                    : (reader.IsDBNull(reader.GetOrdinal("new_block_code")) ? null : reader.GetString(reader.GetOrdinal("new_block_code"))),
                TargetBlockEntitySnapshot: operation == RollbackJobOperation.Rollback
                    ? SqliteAuditValueCodec.ReadBlob(reader, "old_block_entity_blob")
                    : SqliteAuditValueCodec.ReadBlob(reader, "new_block_entity_blob"),
                ExecutionOrder: reader.GetInt32(reader.GetOrdinal("execution_order"))));
        }

        return results;
    }

    public async Task<IReadOnlyList<RollbackJobEntryDetail>> GetRollbackJobEntryDetailsAsync(
        long jobId,
        RollbackJobEntryResult result,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                rje.id AS job_entry_id,
                rje.target_type_code,
                src.source_record_id,
                src.x,
                src.y,
                src.z,
                src.occurred_at_ms,
                rje.result_code,
                rje.error_text
            FROM rollback_job_entries rje
            INNER JOIN (
                SELECT id AS source_record_id, 'block' AS target_type_code, x, y, z, occurred_at_ms FROM block_events
                UNION ALL
                SELECT id AS source_record_id, 'container' AS target_type_code, x, y, z, occurred_at_ms FROM container_transactions
            ) src
                ON src.source_record_id = rje.target_event_id
               AND src.target_type_code = rje.target_type_code
            WHERE rje.job_id = $jobId
              AND rje.result_code = $resultCode
            ORDER BY rje.execution_order
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$resultCode", SqliteAuditValueCodec.ToCode(result));
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<RollbackJobEntryDetail>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RollbackJobEntryDetail(
                JobEntryId: reader.GetInt64(reader.GetOrdinal("job_entry_id")),
                TargetType: SqliteAuditValueCodec.ParseRollbackJobEntryTargetType(reader.GetString(reader.GetOrdinal("target_type_code"))),
                SourceRecordId: reader.GetInt64(reader.GetOrdinal("source_record_id")),
                Position: new BlockPosition(
                    reader.GetInt32(reader.GetOrdinal("x")),
                    reader.GetInt32(reader.GetOrdinal("y")),
                    reader.GetInt32(reader.GetOrdinal("z"))),
                SourceOccurredAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("occurred_at_ms"))),
                Result: SqliteAuditValueCodec.ParseRollbackJobEntryResult(reader.GetString(reader.GetOrdinal("result_code"))),
                ErrorText: reader.IsDBNull(reader.GetOrdinal("error_text")) ? null : reader.GetString(reader.GetOrdinal("error_text"))));
        }

        return results;
    }

    public async Task UpdateRollbackJobStateAsync(
        long jobId,
        RollbackJobState state,
        DateTimeOffset? queuedAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        string? lastError,
        CancellationToken cancellationToken)
    {
        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE rollback_jobs
            SET
                state_code = $stateCode,
                queued_at_ms = CASE WHEN $queuedAt IS NULL THEN queued_at_ms ELSE $queuedAt END,
                started_at_ms = CASE WHEN $startedAt IS NULL THEN started_at_ms ELSE $startedAt END,
                completed_at_ms = CASE WHEN $completedAt IS NULL THEN completed_at_ms ELSE $completedAt END,
                last_error = CASE WHEN $lastError IS NULL THEN last_error ELSE $lastError END
            WHERE id = $jobId;
            """;
        command.Parameters.AddWithValue("$stateCode", SqliteAuditValueCodec.ToCode(state));
        command.Parameters.AddWithValue("$queuedAt", queuedAt.HasValue ? queuedAt.Value.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", startedAt.HasValue ? startedAt.Value.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", completedAt.HasValue ? completedAt.Value.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("$lastError", string.IsNullOrWhiteSpace(lastError) ? DBNull.Value : lastError);
        command.Parameters.AddWithValue("$jobId", jobId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordRollbackBatchAsync(long jobId, IReadOnlyList<RollbackBatchEntryUpdate> updates, CancellationToken cancellationToken)
    {
        if (updates.Count == 0)
        {
            return;
        }

        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var appliedBlockCount = 0;
        var appliedContainerCount = 0;
        var conflictBlockCount = 0;
        var conflictContainerCount = 0;
        var failedBlockCount = 0;
        var failedContainerCount = 0;
        string? lastError = null;
        var processedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var update in updates)
        {
            await using var entryCommand = connection.CreateCommand();
            entryCommand.Transaction = transaction;
            entryCommand.CommandText =
                """
                UPDATE rollback_job_entries
                SET
                    result_code = $resultCode,
                    applied_at_ms = $appliedAt,
                    error_text = $errorText
                WHERE id = $jobEntryId;
                """;
            entryCommand.Parameters.AddWithValue("$resultCode", SqliteAuditValueCodec.ToCode(update.Result));
            entryCommand.Parameters.AddWithValue("$appliedAt", processedAt);
            entryCommand.Parameters.AddWithValue("$errorText", string.IsNullOrWhiteSpace(update.ErrorText) ? DBNull.Value : update.ErrorText);
            entryCommand.Parameters.AddWithValue("$jobEntryId", update.JobEntryId);
            await entryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            switch (update.Result)
            {
                case RollbackJobEntryResult.Applied:
                    if (update.TargetType == RollbackJobEntryTargetType.Block)
                    {
                        appliedBlockCount++;
                    }
                    else
                    {
                        appliedContainerCount++;
                    }

                    break;
                case RollbackJobEntryResult.Conflict:
                    if (update.TargetType == RollbackJobEntryTargetType.Block)
                    {
                        conflictBlockCount++;
                    }
                    else
                    {
                        conflictContainerCount++;
                    }

                    lastError = update.ErrorText;
                    break;
                case RollbackJobEntryResult.Failed:
                    if (update.TargetType == RollbackJobEntryTargetType.Block)
                    {
                        failedBlockCount++;
                    }
                    else
                    {
                        failedContainerCount++;
                    }

                    lastError = update.ErrorText;
                    break;
            }
        }

        await using var jobCommand = connection.CreateCommand();
        jobCommand.Transaction = transaction;
        jobCommand.CommandText =
            """
            UPDATE rollback_jobs
            SET
                applied_block_count = applied_block_count + $appliedBlockCount,
                applied_container_count = applied_container_count + $appliedContainerCount,
                conflict_block_count = conflict_block_count + $conflictBlockCount,
                conflict_container_count = conflict_container_count + $conflictContainerCount,
                failed_block_count = failed_block_count + $failedBlockCount,
                failed_container_count = failed_container_count + $failedContainerCount,
                last_error = CASE WHEN $lastError IS NULL THEN last_error ELSE $lastError END
            WHERE id = $jobId;
            """;
        jobCommand.Parameters.AddWithValue("$appliedBlockCount", appliedBlockCount);
        jobCommand.Parameters.AddWithValue("$appliedContainerCount", appliedContainerCount);
        jobCommand.Parameters.AddWithValue("$conflictBlockCount", conflictBlockCount);
        jobCommand.Parameters.AddWithValue("$conflictContainerCount", conflictContainerCount);
        jobCommand.Parameters.AddWithValue("$failedBlockCount", failedBlockCount);
        jobCommand.Parameters.AddWithValue("$failedContainerCount", failedContainerCount);
        jobCommand.Parameters.AddWithValue("$lastError", string.IsNullOrWhiteSpace(lastError) ? DBNull.Value : lastError);
        jobCommand.Parameters.AddWithValue("$jobId", jobId);
        await jobCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RollbackContainerPlanEntry>> GetRollbackContainerPlanEntriesAsync(long jobId, CancellationToken cancellationToken)
    {
        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                rje.id AS job_entry_id,
                rje.job_id,
                rje.execution_order,
                rj.operation_code,
                ct.id AS source_transaction_id,
                w.code AS world_code,
                ct.x,
                ct.y,
                ct.z,
                ct.chunk_x,
                ct.chunk_y,
                ct.chunk_z,
                ct.occurred_at_ms,
                it.code AS inventory_type_code,
                ct.container_label,
                ct.before_snapshot_blob,
                ct.after_snapshot_blob
            FROM rollback_job_entries rje
            INNER JOIN rollback_jobs rj ON rj.id = rje.job_id
            INNER JOIN container_transactions ct ON ct.id = rje.target_event_id
            INNER JOIN worlds w ON w.id = ct.world_id
            INNER JOIN inventory_types it ON it.id = ct.inventory_type_id
            WHERE rje.job_id = $jobId
              AND rje.target_type_code = 'container'
              AND rje.result_code = 'planned'
            ORDER BY rje.execution_order;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);

        var results = new List<RollbackContainerPlanEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var operation = SqliteAuditValueCodec.ParseRollbackJobOperation(reader.GetString(reader.GetOrdinal("operation_code")));
            results.Add(new RollbackContainerPlanEntry(
                JobEntryId: reader.GetInt64(reader.GetOrdinal("job_entry_id")),
                JobId: reader.GetInt64(reader.GetOrdinal("job_id")),
                SourceTransactionId: reader.GetInt64(reader.GetOrdinal("source_transaction_id")),
                WorldId: reader.GetString(reader.GetOrdinal("world_code")),
                Operation: operation,
                Position: new BlockPosition(
                    reader.GetInt32(reader.GetOrdinal("x")),
                    reader.GetInt32(reader.GetOrdinal("y")),
                    reader.GetInt32(reader.GetOrdinal("z"))),
                Chunk: new ChunkPosition(
                    reader.GetInt32(reader.GetOrdinal("chunk_x")),
                    reader.GetInt32(reader.GetOrdinal("chunk_y")),
                    reader.GetInt32(reader.GetOrdinal("chunk_z"))),
                SourceOccurredAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("occurred_at_ms"))),
                InventoryType: reader.GetString(reader.GetOrdinal("inventory_type_code")),
                ContainerLabel: reader.IsDBNull(reader.GetOrdinal("container_label")) ? null : reader.GetString(reader.GetOrdinal("container_label")),
                ExpectedSnapshot: operation == RollbackJobOperation.Rollback
                    ? SqliteAuditValueCodec.ReadBlob(reader, "after_snapshot_blob")
                    : SqliteAuditValueCodec.ReadBlob(reader, "before_snapshot_blob"),
                TargetSnapshot: operation == RollbackJobOperation.Rollback
                    ? SqliteAuditValueCodec.ReadBlob(reader, "before_snapshot_blob")
                    : SqliteAuditValueCodec.ReadBlob(reader, "after_snapshot_blob"),
                ExecutionOrder: reader.GetInt32(reader.GetOrdinal("execution_order"))));
        }

        return results;
    }
}
