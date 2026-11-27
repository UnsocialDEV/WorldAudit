using Microsoft.Data.Sqlite;
using WorldAudit.Application.Services;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal sealed class SqliteRollbackPreview(SqliteAuditRepositoryState state, SqliteAuditLookupResolver resolver)
{
    internal async Task<long> CreateAsync(RollbackPreviewSelection selection, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(selection.BatchSize);
        await using var connection = await state.ConnectionFactory.OpenConnectionAsync(token, commandTimeoutSeconds: null, privateCache: true).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA temp_store=FILE";
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await SqlitePreviewTargets.StageAsync(connection, selection, state.Options.ChunkSize, token).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        command.Transaction = transaction;
        var request = new RollbackJobCreateRequest(selection.World, selection.RequestedBy, selection.Operation,
            selection.Summary, RollbackJobState.Previewed, 0, 0, 0, null);
        var job = await SqliteRollbackJobHeader.InsertAsync(connection, transaction, resolver, request, token).ConfigureAwait(false);
        command.Parameters.AddWithValue("$job", job);
        command.CommandText = """
            UPDATE rollback_jobs SET planned_block_count=(SELECT count(*) FROM targets WHERE kind='block'),
                planned_container_count=(SELECT count(*) FROM targets WHERE kind='container'),
                planned_visit_count=(SELECT count(DISTINCT coalesce(vm.visit_id,t.id)) FROM targets t
                    LEFT JOIN container_visit_members vm ON vm.transaction_id=t.id WHERE t.kind='container'),
                planned_chunk_count=(SELECT count(*) FROM (SELECT DISTINCT cx,cy,cz FROM targets)),
                oldest_target_occurred_at_ms=(SELECT min(ms) FROM targets) WHERE id=$job;
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        command.CommandText = """
            INSERT INTO rollback_job_entries(job_id,target_type_code,target_event_id,execution_order,reverse_source)
            SELECT $job,kind,id,rowid-1,$reverse FROM ordered WHERE rowid > $after ORDER BY rowid LIMIT $size;
            """;
        command.Parameters.AddWithValue("$size", selection.BatchSize);
        command.Parameters.AddWithValue("$reverse", selection.UndoJobId.HasValue);
        var after = command.Parameters.Add("$after", SqliteType.Integer);
        for (long copied = 0; ; copied += selection.BatchSize)
        {
            after.Value = copied;
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) < selection.BatchSize) break;
        }
        command.CommandText = "DROP TABLE temp.ordered; DROP TABLE temp.targets;";
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        resolver.PublishCommittedLookups(transaction);
        return job;
    }
}
