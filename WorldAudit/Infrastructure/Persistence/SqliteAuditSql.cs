using Microsoft.Data.Sqlite;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal static class SqliteAuditSql
{
    private const int ActorFilterBit = 1;
    private const int CauseFilterBit = 2;
    private const int ActionFilterBit = 4;

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
            be.source_event_id
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
            ct.source_job_id
        FROM container_transactions ct
        INNER JOIN worlds w ON w.id = ct.world_id
        INNER JOIN actors a ON a.id = ct.actor_id
        INNER JOIN causes c ON c.id = ct.cause_id
        INNER JOIN inventory_types it ON it.id = ct.inventory_type_id

        """;

    private const string TimeRangeBlockSql =
        """
          AND ($fromInclusive IS NULL OR be.occurred_at_ms >= $fromInclusive)
          AND ($toInclusive IS NULL OR be.occurred_at_ms <= $toInclusive)
        """;

    private const string TimeRangeContainerSql =
        """
          AND ($fromInclusive IS NULL OR ct.occurred_at_ms >= $fromInclusive)
          AND ($toInclusive IS NULL OR ct.occurred_at_ms <= $toInclusive)
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

    public static string[] BlockLookupSqlByMask { get; } =
    [
        BuildBlockLookupSql(0),
        BuildBlockLookupSql(ActorFilterBit),
        BuildBlockLookupSql(CauseFilterBit),
        BuildBlockLookupSql(ActorFilterBit | CauseFilterBit),
        BuildBlockLookupSql(ActionFilterBit),
        BuildBlockLookupSql(ActorFilterBit | ActionFilterBit),
        BuildBlockLookupSql(CauseFilterBit | ActionFilterBit),
        BuildBlockLookupSql(ActorFilterBit | CauseFilterBit | ActionFilterBit)
    ];

    public static string[] RollbackBlockLookupSqlByMask { get; } =
    [
        BuildBlockLookupSql(0),
        BuildBlockLookupSql(ActorFilterBit),
        BuildBlockLookupSql(CauseFilterBit),
        BuildBlockLookupSql(ActorFilterBit | CauseFilterBit),
        BuildBlockLookupSql(ActionFilterBit),
        BuildBlockLookupSql(ActorFilterBit | ActionFilterBit),
        BuildBlockLookupSql(CauseFilterBit | ActionFilterBit),
        BuildBlockLookupSql(ActorFilterBit | CauseFilterBit | ActionFilterBit)
    ];

    public static string[] ContainerLookupSqlByMask { get; } =
    [
        BuildContainerLookupSql(0),
        BuildContainerLookupSql(ActorFilterBit),
        BuildContainerLookupSql(CauseFilterBit),
        BuildContainerLookupSql(ActorFilterBit | CauseFilterBit)
    ];

    public static string BuildBlockHistorySql(bool includeLimit)
    {
        return BaseSelectSql +
               """
               WHERE be.world_id = $worldId
                 AND be.x = $x
                 AND be.y = $y
                 AND be.z = $z
               """ +
               TimeRangeBlockSql +
               """
               ORDER BY be.occurred_at_ms DESC
               """ +
               (includeLimit ? "\nLIMIT $limit;" : ";");
    }

    public static string BuildContainerHistorySql(bool includeLimit)
    {
        return BaseContainerSql +
               """
               WHERE ct.world_id = $worldId
                 AND ct.x = $x
                 AND ct.y = $y
                 AND ct.z = $z
               """ +
               TimeRangeContainerSql +
               """
               ORDER BY ct.occurred_at_ms DESC
               """ +
               (includeLimit ? "\nLIMIT $limit;" : ";");
    }

    public static int BuildFilterMask(bool hasActor, bool hasCause, bool hasAction)
    {
        var mask = 0;
        if (hasActor)
        {
            mask |= ActorFilterBit;
        }

        if (hasCause)
        {
            mask |= CauseFilterBit;
        }

        if (hasAction)
        {
            mask |= ActionFilterBit;
        }

        return mask;
    }

    public static void AddTimeRangeParameters(SqliteCommand command, AuditTimeRange? timeRange)
    {
        command.Parameters.AddWithValue("$fromInclusive", timeRange?.FromInclusive?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$toInclusive", timeRange?.ToInclusive?.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
    }

    public static void AddLookupParameters(
        SqliteCommand command,
        BlockPosition min,
        BlockPosition max,
        ChunkPosition minChunk,
        ChunkPosition maxChunk,
        AuditTimeRange? timeRange,
        int? limit,
        ResolvedLookupFilters filters)
    {
        command.Parameters.AddWithValue("$minChunkX", minChunk.X);
        command.Parameters.AddWithValue("$maxChunkX", maxChunk.X);
        command.Parameters.AddWithValue("$minChunkZ", minChunk.Z);
        command.Parameters.AddWithValue("$maxChunkZ", maxChunk.Z);
        command.Parameters.AddWithValue("$minX", min.X);
        command.Parameters.AddWithValue("$maxX", max.X);
        command.Parameters.AddWithValue("$minY", min.Y);
        command.Parameters.AddWithValue("$maxY", max.Y);
        command.Parameters.AddWithValue("$minZ", min.Z);
        command.Parameters.AddWithValue("$maxZ", max.Z);
        AddTimeRangeParameters(command, timeRange);

        if (limit.HasValue)
        {
            command.Parameters.AddWithValue("$limit", limit.Value);
        }

        if (filters.ActorId.HasValue)
        {
            command.Parameters.AddWithValue("$actorId", filters.ActorId.Value);
        }

        if (filters.CauseId.HasValue)
        {
            command.Parameters.AddWithValue("$causeId", filters.CauseId.Value);
        }

        if (filters.ActionId.HasValue)
        {
            command.Parameters.AddWithValue("$actionId", filters.ActionId.Value);
        }
    }

    private static string BuildBlockLookupSql(int filterMask)
    {
        var sql =
            BaseSelectSql +
            """
            WHERE be.world_id = $worldId
              AND be.chunk_x BETWEEN $minChunkX AND $maxChunkX
              AND be.chunk_z BETWEEN $minChunkZ AND $maxChunkZ
              AND be.x BETWEEN $minX AND $maxX
              AND be.y BETWEEN $minY AND $maxY
              AND be.z BETWEEN $minZ AND $maxZ
            """ +
            TimeRangeBlockSql;

        if ((filterMask & ActorFilterBit) != 0)
        {
            sql += "\n  AND be.actor_id = $actorId";
        }

        if ((filterMask & CauseFilterBit) != 0)
        {
            sql += "\n  AND be.cause_id = $causeId";
        }

        if ((filterMask & ActionFilterBit) != 0)
        {
            sql += "\n  AND be.action_id = $actionId";
        }

        sql += "\nORDER BY be.occurred_at_ms DESC;";
        return sql;
    }

    private static string BuildContainerLookupSql(int filterMask)
    {
        var sql =
            BaseContainerSql +
            """
            WHERE ct.world_id = $worldId
              AND ct.chunk_x BETWEEN $minChunkX AND $maxChunkX
              AND ct.chunk_z BETWEEN $minChunkZ AND $maxChunkZ
              AND ct.x BETWEEN $minX AND $maxX
              AND ct.y BETWEEN $minY AND $maxY
              AND ct.z BETWEEN $minZ AND $maxZ
            """ +
            TimeRangeContainerSql;

        if ((filterMask & ActorFilterBit) != 0)
        {
            sql += "\n  AND ct.actor_id = $actorId";
        }

        if ((filterMask & CauseFilterBit) != 0)
        {
            sql += "\n  AND ct.cause_id = $causeId";
        }

        sql += "\nORDER BY ct.occurred_at_ms DESC;";
        return sql;
    }
}
