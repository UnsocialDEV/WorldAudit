using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal sealed class SqliteContainerInspectHistory(SqliteConnectionFactory connections)
{
    internal async Task<IReadOnlyList<ContainerInspectEntry>> ReadAsync(string world, BlockPosition? position, int limit, long? jobId = null, int page = 1)
    {
        await using var connection = await connections.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Limit visits before reading their items. A single long visit can span many raw history pages.
        var scope = jobId.HasValue
            ? "ct.id IN (SELECT target_event_id FROM rollback_job_entries WHERE job_id=$job AND target_type_code='container')"
            : "ct.x=$x AND ct.y=$y AND ct.z=$z";
        var memberScope = jobId.HasValue
            ? "WHERE vm.transaction_id IN (SELECT target_event_id FROM rollback_job_entries WHERE job_id=$job AND target_type_code='container')"
            : "";
        command.CommandText = $"""
            WITH recent AS MATERIALIZED (
                SELECT max(ct.id) AS anchor, vm.visit_id, min(ct.occurred_at_ms) AS first_ms,
                       max(ct.occurred_at_ms) AS last_ms, count(*) AS changes
                FROM container_transactions ct
                LEFT JOIN container_visit_members vm ON vm.transaction_id = ct.id
                WHERE ct.world_id = (SELECT id FROM worlds WHERE code = $world)
                  AND {scope}
                GROUP BY coalesce(vm.visit_id, ct.id)
                ORDER BY last_ms DESC, anchor DESC LIMIT $limit OFFSET $offset
            ), members AS (
                SELECT anchor, anchor AS id FROM recent WHERE visit_id IS NULL
                UNION ALL
                SELECT r.anchor, vm.transaction_id FROM recent r
                JOIN container_visit_members vm ON vm.visit_id = r.visit_id
                {memberScope}
            ), totals AS (
                SELECT m.anchor, l.item_id, sum(l.quantity_delta) AS delta
                FROM members m JOIN container_transaction_lines l ON l.transaction_id = m.id
                GROUP BY m.anchor, l.item_id, sign(l.quantity_delta)
            )
            SELECT r.anchor, r.visit_id, coalesce(ct.container_label, it.code), a.name, c.code,
                   r.first_ms, r.last_ms, r.changes, i.code, t.delta, ct.x, ct.y, ct.z
            FROM recent r JOIN container_transactions ct ON ct.id = r.anchor
            JOIN inventory_types it ON it.id = ct.inventory_type_id
            JOIN actors a ON a.id = ct.actor_id JOIN causes c ON c.id = ct.cause_id
            LEFT JOIN totals t ON t.anchor = r.anchor LEFT JOIN items i ON i.id = t.item_id
            ORDER BY r.last_ms DESC, r.anchor DESC, i.code, t.delta
            """;
        command.Parameters.AddWithValue("$world", world);
        if (jobId.HasValue) command.Parameters.AddWithValue("$job", jobId.Value);
        else
        {
            var pos = position ?? throw new ArgumentNullException(nameof(position));
            command.Parameters.AddWithValue("$x", pos.X);
            command.Parameters.AddWithValue("$y", pos.Y);
            command.Parameters.AddWithValue("$z", pos.Z);
        }
        command.Parameters.AddWithValue("$offset", (page - 1L) * limit);
        command.Parameters.AddWithValue("$limit", limit);
        var entries = new List<ContainerInspectEntry>();
        var items = new List<ContainerItemTotal>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var anchor = reader.GetInt64(0);
            if (entries.Count == 0 || entries[^1].DetailId != anchor)
            {
                items = [];
                entries.Add(new(anchor, reader[1] as string, reader.GetString(2), reader.GetString(3),
                    SqliteAuditValueCodec.ParseCause(reader.GetString(4)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)), reader.GetInt64(7), items,
                    new(reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12))));
            }
            if (!reader.IsDBNull(8)) items.Add(new(reader.GetString(8), reader.GetInt64(9)));
        }
        return entries;
    }
}
