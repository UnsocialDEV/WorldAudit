using Microsoft.Data.Sqlite;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal static class SqliteHistoryPage
{
    public static void Configure(SqliteCommand command, bool container, long worldId, BlockPosition center,
        int radius, int chunkSize, AuditTimeRange? timeRange, int limit, ResolvedLookupFilters? filters,
        IReadOnlyList<string>? includeCodes, IReadOnlyList<string>? excludeCodes, bool skipFlameOnlyChanges = false,
        bool excludeJobHistory = false)
    {
        var table = container ? "container_transactions" : "block_events";
        var projection = container ? SqliteAuditSql.BaseContainerSelectSql : SqliteAuditSql.BaseBlockSelectSql;
        var alias = container ? "ct" : "be";
        var predicate = SqliteHistoryPredicate.Build(command, container, worldId, center, radius, chunkSize, timeRange,
            filters, includeCodes, excludeCodes, skipFlameOnlyChanges, excludeJobHistory);
        command.Parameters.AddWithValue("$limit", Math.Max(0, limit));
        command.CommandText = $"""
            WITH page AS MATERIALIZED (
                SELECT id FROM {table} AS candidate
                WHERE {predicate}
                ORDER BY occurred_at_ms DESC, id DESC LIMIT $limit
            )
            {projection}
            WHERE {alias}.id IN (SELECT id FROM page)
            ORDER BY {alias}.occurred_at_ms DESC, {alias}.id DESC;
            """;

    }
}
