using Microsoft.Data.Sqlite;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal sealed class SqliteAuditWriteStore
{
    private readonly SqliteAuditRepositoryState _state;
    private readonly SqliteAuditLookupResolver _resolver;

    public SqliteAuditWriteStore(SqliteAuditRepositoryState state, SqliteAuditLookupResolver resolver)
    {
        _state = state;
        _resolver = resolver;
    }

    public async Task WriteBlockEventsAsync(IReadOnlyList<BlockAuditEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var auditEvent in events)
        {
            var worldId = await _resolver.ResolveWorldIdAsync(connection, transaction, auditEvent.WorldId, cancellationToken).ConfigureAwait(false);
            var actorId = await _resolver.ResolveActorIdAsync(connection, transaction, auditEvent.Actor, cancellationToken).ConfigureAwait(false);
            var causeId = await _resolver.ResolveCauseIdAsync(connection, transaction, auditEvent.Cause, cancellationToken).ConfigureAwait(false);
            var actionId = await _resolver.ResolveActionIdAsync(connection, transaction, auditEvent.Action, cancellationToken).ConfigureAwait(false);
            var oldBlockId = await _resolver.ResolveOptionalBlockIdAsync(connection, transaction, auditEvent.OldBlockCode, cancellationToken).ConfigureAwait(false);
            var newBlockId = await _resolver.ResolveOptionalBlockIdAsync(connection, transaction, auditEvent.NewBlockCode, cancellationToken).ConfigureAwait(false);
            var chunk = auditEvent.Position.ToChunkPosition(_state.Options.ChunkSize);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO block_events (
                    world_id, x, y, z, chunk_x, chunk_y, chunk_z, occurred_at_ms, actor_id, cause_id, action_id,
                    old_block_id, new_block_id, old_block_entity_blob, new_block_entity_blob, flags, source_job_id, source_event_id)
                VALUES (
                    $worldId, $x, $y, $z, $chunkX, $chunkY, $chunkZ, $occurredAt, $actorId, $causeId, $actionId,
                    $oldBlockId, $newBlockId, $oldBlockEntityBlob, $newBlockEntityBlob, $flags, $sourceJobId, $sourceEventId);
                """;

            command.Parameters.AddWithValue("$worldId", worldId);
            command.Parameters.AddWithValue("$x", auditEvent.Position.X);
            command.Parameters.AddWithValue("$y", auditEvent.Position.Y);
            command.Parameters.AddWithValue("$z", auditEvent.Position.Z);
            command.Parameters.AddWithValue("$chunkX", chunk.X);
            command.Parameters.AddWithValue("$chunkY", chunk.Y);
            command.Parameters.AddWithValue("$chunkZ", chunk.Z);
            command.Parameters.AddWithValue("$occurredAt", auditEvent.OccurredAt.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$actorId", actorId);
            command.Parameters.AddWithValue("$causeId", causeId);
            command.Parameters.AddWithValue("$actionId", actionId);
            command.Parameters.AddWithValue("$oldBlockId", oldBlockId.HasValue ? oldBlockId.Value : DBNull.Value);
            command.Parameters.AddWithValue("$newBlockId", newBlockId.HasValue ? newBlockId.Value : DBNull.Value);
            command.Parameters.AddWithValue("$oldBlockEntityBlob", auditEvent.OldBlockEntitySnapshot is null ? DBNull.Value : auditEvent.OldBlockEntitySnapshot);
            command.Parameters.AddWithValue("$newBlockEntityBlob", auditEvent.NewBlockEntitySnapshot is null ? DBNull.Value : auditEvent.NewBlockEntitySnapshot);
            command.Parameters.AddWithValue("$flags", auditEvent.Flags);
            command.Parameters.AddWithValue("$sourceJobId", auditEvent.SourceJobId.HasValue ? auditEvent.SourceJobId.Value : DBNull.Value);
            command.Parameters.AddWithValue("$sourceEventId", auditEvent.SourceEventId.HasValue ? auditEvent.SourceEventId.Value : DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteContainerTransactionsAsync(IReadOnlyList<ContainerAuditTransaction> transactions, CancellationToken cancellationToken)
    {
        if (transactions.Count == 0)
        {
            return;
        }

        await using var connection = await _state.ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var auditTransaction in transactions)
        {
            var worldId = await _resolver.ResolveWorldIdAsync(connection, transaction, auditTransaction.WorldId, cancellationToken).ConfigureAwait(false);
            var actorId = await _resolver.ResolveActorIdAsync(connection, transaction, auditTransaction.Actor, cancellationToken).ConfigureAwait(false);
            var causeId = await _resolver.ResolveCauseIdAsync(connection, transaction, auditTransaction.Cause, cancellationToken).ConfigureAwait(false);
            var inventoryTypeId = await _resolver.ResolveInventoryTypeIdAsync(connection, transaction, auditTransaction.InventoryType, cancellationToken).ConfigureAwait(false);
            var chunk = auditTransaction.Position.ToChunkPosition(_state.Options.ChunkSize);

            long transactionId;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO container_transactions (
                        world_id, x, y, z, chunk_x, chunk_y, chunk_z, occurred_at_ms, actor_id, cause_id, inventory_type_id,
                        container_label, before_snapshot_blob, after_snapshot_blob, flags, source_job_id)
                    VALUES (
                        $worldId, $x, $y, $z, $chunkX, $chunkY, $chunkZ, $occurredAt, $actorId, $causeId, $inventoryTypeId,
                        $containerLabel, $beforeSnapshotBlob, $afterSnapshotBlob, $flags, $sourceJobId);
                    SELECT last_insert_rowid();
                    """;

                command.Parameters.AddWithValue("$worldId", worldId);
                command.Parameters.AddWithValue("$x", auditTransaction.Position.X);
                command.Parameters.AddWithValue("$y", auditTransaction.Position.Y);
                command.Parameters.AddWithValue("$z", auditTransaction.Position.Z);
                command.Parameters.AddWithValue("$chunkX", chunk.X);
                command.Parameters.AddWithValue("$chunkY", chunk.Y);
                command.Parameters.AddWithValue("$chunkZ", chunk.Z);
                command.Parameters.AddWithValue("$occurredAt", auditTransaction.OccurredAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$actorId", actorId);
                command.Parameters.AddWithValue("$causeId", causeId);
                command.Parameters.AddWithValue("$inventoryTypeId", inventoryTypeId);
                command.Parameters.AddWithValue("$containerLabel", string.IsNullOrWhiteSpace(auditTransaction.ContainerLabel) ? DBNull.Value : auditTransaction.ContainerLabel);
                command.Parameters.AddWithValue("$beforeSnapshotBlob", auditTransaction.BeforeSnapshot is null ? DBNull.Value : auditTransaction.BeforeSnapshot);
                command.Parameters.AddWithValue("$afterSnapshotBlob", auditTransaction.AfterSnapshot is null ? DBNull.Value : auditTransaction.AfterSnapshot);
                command.Parameters.AddWithValue("$flags", auditTransaction.Flags);
                command.Parameters.AddWithValue("$sourceJobId", auditTransaction.SourceJobId.HasValue ? auditTransaction.SourceJobId.Value : DBNull.Value);

                transactionId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            }

            foreach (var line in auditTransaction.Lines)
            {
                var itemId = await _resolver.ResolveItemIdAsync(connection, transaction, line.ItemCode, cancellationToken).ConfigureAwait(false);

                await using var lineCommand = connection.CreateCommand();
                lineCommand.Transaction = transaction;
                lineCommand.CommandText =
                    """
                    INSERT INTO container_transaction_lines (
                        transaction_id, item_id, slot_id, quantity_delta, before_quantity, after_quantity, stack_before_blob, stack_after_blob)
                    VALUES (
                        $transactionId, $itemId, $slotId, $quantityDelta, $beforeQuantity, $afterQuantity, $stackBeforeBlob, $stackAfterBlob);
                    """;

                lineCommand.Parameters.AddWithValue("$transactionId", transactionId);
                lineCommand.Parameters.AddWithValue("$itemId", itemId);
                lineCommand.Parameters.AddWithValue("$slotId", line.SlotId.HasValue ? line.SlotId.Value : DBNull.Value);
                lineCommand.Parameters.AddWithValue("$quantityDelta", line.QuantityDelta);
                lineCommand.Parameters.AddWithValue("$beforeQuantity", line.BeforeQuantity);
                lineCommand.Parameters.AddWithValue("$afterQuantity", line.AfterQuantity);
                lineCommand.Parameters.AddWithValue("$stackBeforeBlob", line.StackBeforeSnapshot is null ? DBNull.Value : line.StackBeforeSnapshot);
                lineCommand.Parameters.AddWithValue("$stackAfterBlob", line.StackAfterSnapshot is null ? DBNull.Value : line.StackAfterSnapshot);
                await lineCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
