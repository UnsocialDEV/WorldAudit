using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal sealed class SqliteJobVisitDetails(SqliteConnectionFactory connections)
{
    internal async Task<(int Count, IReadOnlyList<ContainerAuditTransaction> Rows)> ReadAsync(
        long job, string world, long anchor, int page, int size)
    {
        using var connection = await connections.OpenConnectionAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        // Both the anchor and the displayed transactions must belong to this frozen job.
        const string members = """
            WITH selected AS (
                SELECT e.target_event_id AS id FROM rollback_job_entries e JOIN rollback_jobs j ON j.id=e.job_id
                WHERE j.id=$job AND j.world_id=(SELECT id FROM worlds WHERE code=$world) AND e.target_type_code='container'
            ), members AS (
                SELECT id FROM selected WHERE id=$anchor
                UNION
                SELECT m.transaction_id FROM container_visit_members a JOIN container_visit_members m ON m.visit_id=a.visit_id
                WHERE a.transaction_id=$anchor AND a.transaction_id IN selected AND m.transaction_id IN selected
            )
            """;
        command.Parameters.AddWithValue("$job", job);
        command.Parameters.AddWithValue("$world", world);
        command.Parameters.AddWithValue("$anchor", anchor);
        command.CommandText = members + " SELECT count(*) FROM members";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
        command.CommandText = $"""
            {members}, page AS MATERIALIZED (
                SELECT ct.id FROM container_transactions ct JOIN members ON members.id=ct.id
                ORDER BY ct.occurred_at_ms DESC, ct.id DESC LIMIT $size OFFSET $offset
            )
            {SqliteAuditSql.BaseContainerSelectSql}
            JOIN page ON page.id=ct.id ORDER BY ct.occurred_at_ms DESC, ct.id DESC;
            """;
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$offset", (page - 1L) * size);
        return (count, await new SqliteAuditRecordReader().ReadContainerTransactionsAsync(connection, command, default).ConfigureAwait(false));
    }
}
