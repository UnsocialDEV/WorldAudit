namespace WorldAudit.Infrastructure.Persistence;

internal static class SqliteAuditSql
{
    private const string BaseSelectSql =
        """
        SELECT
            be.id,
            w.code AS world_code,
            be.x,
            be.y,
            be.z,
            be.occurred_at_ms,
            a.name AS actor_name,
            a.external_id,
            a.is_synthetic,
            c.code AS cause_code,
            act.code AS action_code,
            oldb.code AS old_block_code,
            newb.code AS new_block_code,
            be.old_block_entity_blob,
            be.new_block_entity_blob,
            be.flags,
            be.source_job_id,
            be.source_event_id,
            be.layer, be.operation_id
        FROM block_events be
        INNER JOIN worlds w ON w.id = be.world_id
        INNER JOIN actors a ON a.id = be.actor_id
        INNER JOIN causes c ON c.id = be.cause_id
        INNER JOIN actions act ON act.id = be.action_id
        LEFT JOIN blocks oldb ON oldb.id = be.old_block_id
        LEFT JOIN blocks newb ON newb.id = be.new_block_id

        """;

    private const string BaseContainerSql =
        """
        SELECT
            ct.id,
            w.code AS world_code,
            ct.x,
            ct.y,
            ct.z,
            ct.occurred_at_ms,
            a.name AS actor_name,
            a.external_id,
            a.is_synthetic,
            c.code AS cause_code,
            it.code AS inventory_type_code,
            ct.container_label,
            ct.before_snapshot_blob,
            ct.after_snapshot_blob,
            ct.flags,
            ct.source_job_id, ct.operation_id, vm.visit_id
        FROM container_transactions ct
        LEFT JOIN container_visit_members vm ON vm.transaction_id = ct.id
        INNER JOIN worlds w ON w.id = ct.world_id
        INNER JOIN actors a ON a.id = ct.actor_id
        INNER JOIN causes c ON c.id = ct.cause_id
        INNER JOIN inventory_types it ON it.id = ct.inventory_type_id

        """;

    public static string BaseBlockSelectSql => BaseSelectSql;

    public static string BaseContainerSelectSql => BaseContainerSql;

    public static string BaseRollbackJobSelectSql =>
        """
        SELECT
            rj.id,
            w.code AS world_code,
            a.name AS actor_name,
            a.external_id,
            a.is_synthetic,
            rj.operation_code,
            rj.filter_summary,
            rj.state_code,
            rj.created_at_ms,
            rj.queued_at_ms,
            rj.started_at_ms,
            rj.completed_at_ms,
            rj.planned_block_count,
            rj.planned_container_count,
            rj.planned_visit_count,
            rj.planned_chunk_count,
            rj.applied_block_count,
            rj.applied_container_count,
            rj.conflict_block_count,
            rj.conflict_container_count,
            rj.failed_block_count,
            rj.failed_container_count,
            rj.oldest_target_occurred_at_ms,
            rj.last_error
        FROM rollback_jobs rj
        INNER JOIN worlds w ON w.id = rj.world_id
        INNER JOIN actors a ON a.id = rj.requested_by_actor_id

        """;
}
