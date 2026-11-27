using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal sealed class SqliteJobBlockPage(SqliteConnectionFactory connections)
{
    internal async Task<IReadOnlyList<BlockAuditEvent>> ReadAsync(long job, string world, int page, int size)
    {
        using var connection = await connections.OpenConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH page AS MATERIALIZED (
                SELECT be.id FROM rollback_job_entries e JOIN block_events be ON be.id=e.target_event_id
                WHERE e.job_id=$job AND e.target_type_code='block'
                ORDER BY be.occurred_at_ms DESC, be.id DESC LIMIT $size OFFSET $offset
            )
            {SqliteAuditSql.BaseBlockSelectSql}
            JOIN page ON page.id=be.id WHERE w.code=$world ORDER BY be.occurred_at_ms DESC, be.id DESC;
            """;
        command.Parameters.AddWithValue("$job", job);
        command.Parameters.AddWithValue("$world", world);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$offset", (page - 1L) * size);
        return await new SqliteAuditRecordReader().ReadBlockEventsAsync(command, default).ConfigureAwait(false);
    }
}
