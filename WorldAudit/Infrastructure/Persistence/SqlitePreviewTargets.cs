using Microsoft.Data.Sqlite;
using WorldAudit.Application.Services;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal static class SqlitePreviewTargets
{
    internal static async Task StageAsync(SqliteConnection connection, RollbackPreviewSelection selection, int chunkSize, CancellationToken token)
    {
        // A private WAL reader freezes both history types while normal audit writes can continue.
        using var snapshot = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = snapshot;
        command.CommandText = "SELECT id FROM worlds WHERE code=$world";
        command.Parameters.AddWithValue("$world", selection.World);
        var world = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as long? ?? -1;
        command.CommandText = """
            CREATE TEMP TABLE targets(kind TEXT, id INTEGER, ms INTEGER, cx INTEGER, cy INTEGER, cz INTEGER,
                x INTEGER, y INTEGER, z INTEGER, covered_op TEXT);
            CREATE INDEX temp.ix_covered ON targets(covered_op,x,y,z) WHERE kind='block' AND covered_op IS NOT NULL;
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (selection.Filters?.IncludeBlocks != false) await StageTypeAsync(false);
        if (selection.Filters?.IncludeContainers != false && selection.Filters?.Action is null) await StageTypeAsync(true);

        command.Parameters.Clear();
        var reverse = selection.Operation == RollbackJobOperation.Rollback || selection.UndoJobId.HasValue;
        var direction = reverse ? "DESC" : "ASC";
        command.CommandText = $"""
            DELETE FROM targets AS t WHERE kind='container' AND covered_op IS NOT NULL AND EXISTS (
                SELECT 1 FROM targets b WHERE b.kind='block' AND b.covered_op=t.covered_op
                AND b.x=t.x AND b.y=t.y AND b.z=t.z);
            CREATE TEMP TABLE ordered AS SELECT kind,id FROM targets
            ORDER BY ms {direction}, kind, CASE WHEN kind='block' THEN cx END,
                CASE WHEN kind='block' THEN cz END, CASE WHEN kind='block' THEN cy END, id {direction};
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await snapshot.CommitAsync(token).ConfigureAwait(false);

        async Task StageTypeAsync(bool container)
        {
            command.Parameters.Clear();
            var kind = container ? "container" : "block";
            var table = container ? "container_transactions" : "block_events";
            var operation = container ? "operation_id" :
                "CASE WHEN layer=1 AND action_id=(SELECT id FROM actions WHERE code='break') AND old_block_entity_blob IS NOT NULL THEN operation_id END";
            string predicate;
            if (selection.UndoJobId is long source)
            {
                predicate = "world_id=$world AND source_job_id=$source";
                command.Parameters.AddWithValue("$world", world);
                command.Parameters.AddWithValue("$source", source);
                operation = "NULL"; // Every actual mutation of the original job must be inverted.
            }
            else
            {
                var f = selection.Filters!;
                var rollback = selection.Operation == RollbackJobOperation.Rollback;
                predicate = SqliteHistoryPredicate.Build(command, container, world, selection.Center, f.Radius ?? 5,
                    chunkSize, f.TimeRange, null, f.IncludeCodes, f.ExcludeCodes, rollback,
                    rollback && f.Cause is not (AuditCause.Rollback or AuditCause.Restore));
                if (f.ActorName is not null)
                {
                    predicate += " AND actor_id=(SELECT id FROM actors WHERE name=$actor)";
                    command.Parameters.AddWithValue("$actor", f.ActorName);
                }
                if (f.Cause is { } cause)
                {
                    predicate += " AND cause_id=(SELECT id FROM causes WHERE code=$cause)";
                    command.Parameters.AddWithValue("$cause", SqliteAuditValueCodec.ToCode(cause));
                }
                if (f.Action is { } action)
                {
                    predicate += " AND action_id=(SELECT id FROM actions WHERE code=$action)";
                    command.Parameters.AddWithValue("$action", SqliteAuditValueCodec.ToCode(action));
                }
            }
            command.CommandText = $"""
                INSERT INTO targets SELECT '{kind}',id,occurred_at_ms,chunk_x,chunk_y,chunk_z,x,y,z,{operation}
                FROM {table} AS candidate WHERE {predicate};
                """;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }
}
