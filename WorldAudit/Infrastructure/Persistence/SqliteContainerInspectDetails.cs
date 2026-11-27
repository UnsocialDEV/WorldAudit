using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal sealed class SqliteContainerInspectDetails(SqliteConnectionFactory connections)
{
    internal async Task<IReadOnlyList<ContainerAuditTransaction>> ReadAsync(string world, long anchor, int page, int pageSize)
    {
        await using var connection = await connections.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH members AS (
                SELECT $anchor AS id
                UNION
                SELECT m.transaction_id FROM container_visit_members a
                JOIN container_visit_members m ON m.visit_id = a.visit_id WHERE a.transaction_id = $anchor
            )
            {SqliteAuditSql.BaseContainerSelectSql}
            JOIN members ON members.id = ct.id
            WHERE w.code = $world
            ORDER BY ct.occurred_at_ms, ct.id LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$world", world);
        command.Parameters.AddWithValue("$anchor", anchor);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        command.Parameters.AddWithValue("$offset", (page - 1L) * pageSize);
        return await new SqliteAuditRecordReader().ReadContainerTransactionsAsync(connection, command, default).ConfigureAwait(false);
    }
}
