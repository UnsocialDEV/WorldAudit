using Microsoft.Data.Sqlite;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal sealed class SqliteAuditRecordReader
{
    public async Task<IReadOnlyList<BlockAuditEvent>> ReadBlockEventsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var results = new List<BlockAuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new BlockAuditEvent(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                WorldId: reader.GetString(reader.GetOrdinal("world_code")),
                Position: new BlockPosition(
                    reader.GetInt32(reader.GetOrdinal("x")),
                    reader.GetInt32(reader.GetOrdinal("y")),
                    reader.GetInt32(reader.GetOrdinal("z"))),
                OccurredAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("occurred_at_ms"))),
                Actor: new AuditActor(
                    reader.GetString(reader.GetOrdinal("actor_name")),
                    reader.IsDBNull(reader.GetOrdinal("external_id")) ? null : reader.GetString(reader.GetOrdinal("external_id")),
                    reader.GetInt64(reader.GetOrdinal("is_synthetic")) == 1),
                Cause: SqliteAuditValueCodec.ParseCause(reader.GetString(reader.GetOrdinal("cause_code"))),
                Action: SqliteAuditValueCodec.ParseAction(reader.GetString(reader.GetOrdinal("action_code"))),
                OldBlockCode: reader.IsDBNull(reader.GetOrdinal("old_block_code")) ? null : reader.GetString(reader.GetOrdinal("old_block_code")),
                NewBlockCode: reader.IsDBNull(reader.GetOrdinal("new_block_code")) ? null : reader.GetString(reader.GetOrdinal("new_block_code")),
                OldBlockEntitySnapshot: SqliteAuditValueCodec.ReadBlob(reader, "old_block_entity_blob"),
                NewBlockEntitySnapshot: SqliteAuditValueCodec.ReadBlob(reader, "new_block_entity_blob"),
                Flags: reader.GetInt32(reader.GetOrdinal("flags")),
                SourceJobId: reader.IsDBNull(reader.GetOrdinal("source_job_id")) ? null : reader.GetInt64(reader.GetOrdinal("source_job_id")),
                SourceEventId: reader.IsDBNull(reader.GetOrdinal("source_event_id")) ? null : reader.GetInt64(reader.GetOrdinal("source_event_id"))));
        }

        return results;
    }

    public async Task<IReadOnlyList<ContainerAuditTransaction>> ReadContainerTransactionsAsync(
        SqliteConnection connection,
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<ContainerTransactionRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ContainerTransactionRow(
                    Id: reader.GetInt64(reader.GetOrdinal("id")),
                    WorldId: reader.GetString(reader.GetOrdinal("world_code")),
                    Position: new BlockPosition(
                        reader.GetInt32(reader.GetOrdinal("x")),
                        reader.GetInt32(reader.GetOrdinal("y")),
                        reader.GetInt32(reader.GetOrdinal("z"))),
                    OccurredAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("occurred_at_ms"))),
                    Actor: new AuditActor(
                        reader.GetString(reader.GetOrdinal("actor_name")),
                        reader.IsDBNull(reader.GetOrdinal("external_id")) ? null : reader.GetString(reader.GetOrdinal("external_id")),
                        reader.GetInt64(reader.GetOrdinal("is_synthetic")) == 1),
                    Cause: SqliteAuditValueCodec.ParseCause(reader.GetString(reader.GetOrdinal("cause_code"))),
                    InventoryType: reader.GetString(reader.GetOrdinal("inventory_type_code")),
                    ContainerLabel: reader.IsDBNull(reader.GetOrdinal("container_label")) ? null : reader.GetString(reader.GetOrdinal("container_label")),
                    BeforeSnapshot: SqliteAuditValueCodec.ReadBlob(reader, "before_snapshot_blob"),
                    AfterSnapshot: SqliteAuditValueCodec.ReadBlob(reader, "after_snapshot_blob"),
                    Flags: reader.GetInt32(reader.GetOrdinal("flags")),
                    SourceJobId: reader.IsDBNull(reader.GetOrdinal("source_job_id")) ? null : reader.GetInt64(reader.GetOrdinal("source_job_id"))));
            }
        }

        if (rows.Count == 0)
        {
            return Array.Empty<ContainerAuditTransaction>();
        }

        var transactionIds = new long[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            transactionIds[index] = rows[index].Id;
        }

        var linesByTransactionId = await LoadContainerLinesAsync(connection, transactionIds, cancellationToken).ConfigureAwait(false);
        var results = new ContainerAuditTransaction[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            results[index] = new ContainerAuditTransaction(
                Id: row.Id,
                WorldId: row.WorldId,
                Position: row.Position,
                OccurredAt: row.OccurredAt,
                Actor: row.Actor,
                Cause: row.Cause,
                InventoryType: row.InventoryType,
                ContainerLabel: row.ContainerLabel,
                BeforeSnapshot: row.BeforeSnapshot,
                AfterSnapshot: row.AfterSnapshot,
                Lines: linesByTransactionId.TryGetValue(row.Id, out var lines) ? lines : Array.Empty<ContainerAuditLine>(),
                Flags: row.Flags,
                SourceJobId: row.SourceJobId);
        }

        return results;
    }

    public async Task<IReadOnlyList<RollbackJob>> ReadRollbackJobsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var results = new List<RollbackJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RollbackJob(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                WorldId: reader.GetString(reader.GetOrdinal("world_code")),
                RequestedBy: new AuditActor(
                    reader.GetString(reader.GetOrdinal("actor_name")),
                    reader.IsDBNull(reader.GetOrdinal("external_id")) ? null : reader.GetString(reader.GetOrdinal("external_id")),
                    reader.GetInt64(reader.GetOrdinal("is_synthetic")) == 1),
                Operation: SqliteAuditValueCodec.ParseRollbackJobOperation(reader.GetString(reader.GetOrdinal("operation_code"))),
                FilterSummary: reader.GetString(reader.GetOrdinal("filter_summary")),
                State: SqliteAuditValueCodec.ParseRollbackJobState(reader.GetString(reader.GetOrdinal("state_code"))),
                CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(reader.GetOrdinal("created_at_ms"))),
                QueuedAt: SqliteAuditValueCodec.ReadNullableUnixMilliseconds(reader, "queued_at_ms"),
                StartedAt: SqliteAuditValueCodec.ReadNullableUnixMilliseconds(reader, "started_at_ms"),
                CompletedAt: SqliteAuditValueCodec.ReadNullableUnixMilliseconds(reader, "completed_at_ms"),
                PlannedBlockCount: reader.GetInt32(reader.GetOrdinal("planned_block_count")),
                PlannedContainerCount: reader.GetInt32(reader.GetOrdinal("planned_container_count")),
                PlannedChunkCount: reader.GetInt32(reader.GetOrdinal("planned_chunk_count")),
                AppliedBlockCount: reader.GetInt32(reader.GetOrdinal("applied_block_count")),
                AppliedContainerCount: reader.GetInt32(reader.GetOrdinal("applied_container_count")),
                ConflictBlockCount: reader.GetInt32(reader.GetOrdinal("conflict_block_count")),
                ConflictContainerCount: reader.GetInt32(reader.GetOrdinal("conflict_container_count")),
                FailedBlockCount: reader.GetInt32(reader.GetOrdinal("failed_block_count")),
                FailedContainerCount: reader.GetInt32(reader.GetOrdinal("failed_container_count")),
                OldestTargetOccurredAt: SqliteAuditValueCodec.ReadNullableUnixMilliseconds(reader, "oldest_target_occurred_at_ms"),
                LastError: reader.IsDBNull(reader.GetOrdinal("last_error")) ? null : reader.GetString(reader.GetOrdinal("last_error"))));
        }

        return results;
    }

    private static async Task<Dictionary<long, IReadOnlyList<ContainerAuditLine>>> LoadContainerLinesAsync(
        SqliteConnection connection,
        IReadOnlyList<long> transactionIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, List<ContainerAuditLine>>();
        if (transactionIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<ContainerAuditLine>>();
        }

        var parameterNames = new List<string>(transactionIds.Count);
        await using var command = connection.CreateCommand();

        for (var index = 0; index < transactionIds.Count; index++)
        {
            var parameterName = $"$transactionId{index}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, transactionIds[index]);
        }

        command.CommandText =
            $"""
            SELECT
                ctl.id,
                ctl.transaction_id,
                i.code AS item_code,
                ctl.slot_id,
                ctl.quantity_delta,
                ctl.before_quantity,
                ctl.after_quantity,
                ctl.stack_before_blob,
                ctl.stack_after_blob
            FROM container_transaction_lines ctl
            INNER JOIN items i ON i.id = ctl.item_id
            WHERE ctl.transaction_id IN ({string.Join(", ", parameterNames)})
            ORDER BY ctl.transaction_id, ctl.id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var transactionId = reader.GetInt64(reader.GetOrdinal("transaction_id"));
            if (!result.TryGetValue(transactionId, out var lines))
            {
                lines = [];
                result[transactionId] = lines;
            }

            lines.Add(new ContainerAuditLine(
                Id: reader.GetInt64(reader.GetOrdinal("id")),
                ItemCode: reader.GetString(reader.GetOrdinal("item_code")),
                SlotId: reader.IsDBNull(reader.GetOrdinal("slot_id")) ? null : reader.GetInt32(reader.GetOrdinal("slot_id")),
                QuantityDelta: reader.GetInt32(reader.GetOrdinal("quantity_delta")),
                BeforeQuantity: reader.GetInt32(reader.GetOrdinal("before_quantity")),
                AfterQuantity: reader.GetInt32(reader.GetOrdinal("after_quantity")),
                StackBeforeSnapshot: SqliteAuditValueCodec.ReadBlob(reader, "stack_before_blob"),
                StackAfterSnapshot: SqliteAuditValueCodec.ReadBlob(reader, "stack_after_blob")));
        }

        var readonlyResult = new Dictionary<long, IReadOnlyList<ContainerAuditLine>>(result.Count);
        foreach (var pair in result)
        {
            readonlyResult[pair.Key] = pair.Value;
        }

        return readonlyResult;
    }

    private sealed record ContainerTransactionRow(
        long Id,
        string WorldId,
        BlockPosition Position,
        DateTimeOffset OccurredAt,
        AuditActor Actor,
        AuditCause Cause,
        string InventoryType,
        string? ContainerLabel,
        byte[]? BeforeSnapshot,
        byte[]? AfterSnapshot,
        int Flags,
        long? SourceJobId);
}
