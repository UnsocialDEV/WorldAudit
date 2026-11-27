using Microsoft.Data.Sqlite;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal static class SqliteRollbackJobHeader
{
    internal static async Task<long> InsertAsync(SqliteConnection connection, SqliteTransaction transaction,
        SqliteAuditLookupResolver resolver, RollbackJobCreateRequest request, CancellationToken cancellationToken)
    {
        var worldId = await resolver.ResolveWorldIdAsync(connection, transaction, request.WorldId, cancellationToken).ConfigureAwait(false);
        var requestedByActorId = await resolver.ResolveActorIdAsync(connection, transaction, request.RequestedBy, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
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

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }
}
