namespace WorldAudit.Infrastructure.Persistence;

internal static class ContainerVisitMigration
{
    internal const string Sql = """
        CREATE TABLE container_visit_members (
            transaction_id INTEGER PRIMARY KEY REFERENCES container_transactions(id) ON DELETE CASCADE,
            visit_id TEXT NOT NULL
        );
        CREATE INDEX ix_container_visit_members_visit ON container_visit_members(visit_id);

        -- The timeline includes other actors and synthetic changes so either can interrupt a run.
        WITH timeline AS (
            SELECT ct.id, ct.world_id, ct.x, ct.y, ct.z, ct.occurred_at_ms,
                   ct.inventory_type_id, ct.container_label,
                   CASE WHEN c.code = 'player' AND a.is_synthetic = 0 AND ct.source_job_id IS NULL
                        THEN ct.actor_id END AS player
            FROM container_transactions ct
            JOIN actors a ON a.id = ct.actor_id
            JOIN causes c ON c.id = ct.cause_id
        ), adjacent AS (
            SELECT *, lag(player) OVER loc AS previous_player,
                   lag(inventory_type_id) OVER loc AS previous_type,
                   lag(container_label) OVER loc AS previous_label,
                   lag(occurred_at_ms) OVER loc AS previous_ms
            FROM timeline
            WINDOW loc AS (PARTITION BY world_id, x, y, z ORDER BY occurred_at_ms, id)
        ), boundaries AS (
            SELECT *, CASE WHEN player = previous_player AND inventory_type_id = previous_type
                         AND container_label IS previous_label AND occurred_at_ms - previous_ms <= 5000
                         AND NOT EXISTS (
                             SELECT 1 FROM block_events b WHERE b.world_id = adjacent.world_id
                             AND b.x = adjacent.x AND b.y = adjacent.y AND b.z = adjacent.z
                             AND b.occurred_at_ms BETWEEN previous_ms AND adjacent.occurred_at_ms
                         ) THEN 0 ELSE 1 END AS starts_group
            FROM adjacent
        ), runs AS (
            SELECT *, sum(starts_group) OVER (
                PARTITION BY world_id, x, y, z ORDER BY occurred_at_ms, id
            ) AS run
            FROM boundaries
        ), members AS (
            SELECT id, min(id) OVER visit AS first_id, count(*) OVER visit AS size
            FROM runs WHERE player IS NOT NULL
            WINDOW visit AS (PARTITION BY world_id, x, y, z, run)
        )
        INSERT INTO container_visit_members(transaction_id, visit_id)
        SELECT id, 'legacy:' || first_id FROM members WHERE size > 1;

        INSERT INTO schema_migrations(version, applied_at_ms) VALUES (10, unixepoch('subsec') * 1000);
        """;
}
