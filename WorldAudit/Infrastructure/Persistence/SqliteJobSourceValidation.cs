namespace WorldAudit.Infrastructure.Persistence;

internal static class SqliteJobSourceValidation
{
    internal static async Task CheckAsync(SqliteConnectionFactory connections, long jobId, CancellationToken token)
    {
        using var connection = await connections.OpenConnectionAsync(token).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM rollback_job_entries e JOIN rollback_jobs j ON j.id=e.job_id
                LEFT JOIN block_events b ON e.target_type_code='block' AND b.id=e.target_event_id
                LEFT JOIN container_transactions c ON e.target_type_code='container' AND c.id=e.target_event_id
                WHERE e.job_id=$job AND e.result_code='planned'
                  AND coalesce(b.world_id,c.world_id) IS NOT j.world_id
            );
            """;
        command.Parameters.AddWithValue("$job", jobId);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0)
            throw new InvalidOperationException("A planned entry is missing its source or belongs to another world.");
    }
}
