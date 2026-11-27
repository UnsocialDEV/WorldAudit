using System.Text.Json;
using Microsoft.Data.Sqlite;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Persistence;

internal static class SqliteHistoryPredicate
{
    public static string Build(SqliteCommand command, bool container, long worldId, BlockPosition center,
        int radius, int chunkSize, AuditTimeRange? timeRange, ResolvedLookupFilters? filters,
        IReadOnlyList<string>? includeCodes, IReadOnlyList<string>? excludeCodes, bool skipFlameOnlyChanges, bool excludeJobHistory)
    {
        var conditions = new List<string> { "world_id = $worldId" };
        var parameters = command.Parameters;
        if (excludeJobHistory)
            conditions.Add("source_job_id IS NULL AND cause_id NOT IN (SELECT id FROM causes WHERE code IN ('rollback', 'restore'))");
        if (!container)
        {
            conditions.Add("(old_block_id IS NULL OR old_block_id IS NOT new_block_id OR old_block_id IS NOT (SELECT id FROM blocks WHERE code = $fire))");
            parameters.AddWithValue("$fire", FireBlock.Code);
            if (skipFlameOnlyChanges)
            {
                conditions.Add("""
                    NOT EXISTS (
                        SELECT 1 FROM blocks WHERE code = $fire AND id IN (candidate.old_block_id, candidate.new_block_id)
                        AND (candidate.old_block_id IS NULL OR candidate.new_block_id IS NULL OR
                            (SELECT id FROM blocks WHERE code = 'game:air') IN (candidate.old_block_id, candidate.new_block_id))
                    )
                    """);
            }
        }
        parameters.AddWithValue("$worldId", worldId);
        parameters.AddWithValue("$cx", center.X);
        parameters.AddWithValue("$cy", center.Y);
        parameters.AddWithValue("$cz", center.Z);
        if (radius == 0)
            conditions.Add("x = $cx AND y = $cy AND z = $cz");
        else
        {
            conditions.AddRange([
                "chunk_x BETWEEN $minChunkX AND $maxChunkX", "chunk_z BETWEEN $minChunkZ AND $maxChunkZ",
                "x BETWEEN $cx - $radius AND $cx + $radius", "y BETWEEN $cy - $radius AND $cy + $radius",
                "z BETWEEN $cz - $radius AND $cz + $radius", "y / 32768 = $dimension",
                "(x-$cx)*(x-$cx) + (y-$cy)*(y-$cy) + (z-$cz)*(z-$cz) <= $radiusSquared"
            ]);
            var min = new BlockPosition((int)Math.Max(int.MinValue, (long)center.X - radius), center.Y,
                (int)Math.Max(int.MinValue, (long)center.Z - radius)).ToChunkPosition(chunkSize);
            var max = new BlockPosition((int)Math.Min(int.MaxValue, (long)center.X + radius), center.Y,
                (int)Math.Min(int.MaxValue, (long)center.Z + radius)).ToChunkPosition(chunkSize);
            parameters.AddWithValue("$minChunkX", min.X);
            parameters.AddWithValue("$maxChunkX", max.X);
            parameters.AddWithValue("$minChunkZ", min.Z);
            parameters.AddWithValue("$maxChunkZ", max.Z);
            parameters.AddWithValue("$radius", radius);
            parameters.AddWithValue("$radiusSquared", (long)radius * radius);
            parameters.AddWithValue("$dimension", center.Dimension);
        }
        AddPredicate("occurred_at_ms >= $from", "$from", timeRange?.FromInclusive?.ToUnixTimeMilliseconds());
        AddPredicate("occurred_at_ms <= $to", "$to", timeRange?.ToInclusive?.ToUnixTimeMilliseconds());
        AddPredicate("actor_id = $actor", "$actor", filters?.ActorId);
        AddPredicate("cause_id = $cause", "$cause", filters?.CauseId);
        AddPredicate("action_id = $action", "$action", filters?.ActionId);
        AddCodes(includeCodes, "$includes", false);
        AddCodes(excludeCodes, "$excludes", true);
        return string.Join(" AND ", conditions);

        void AddPredicate(string clause, string parameter, long? value)
        {
            if (!value.HasValue) return;
            conditions.Add(clause);
            parameters.AddWithValue(parameter, value.Value);
        }

        void AddCodes(IReadOnlyList<string>? codes, string parameter, bool exclude)
        {
            if (codes is not { Count: > 0 }) return;
            parameters.AddWithValue(parameter, JsonSerializer.Serialize(codes));
            var matches = $"SELECT value FROM json_each({parameter})";
            var predicate = container
                ? $"EXISTS (SELECT 1 FROM container_transaction_lines l JOIN items i ON i.id = l.item_id WHERE l.transaction_id = candidate.id AND i.code COLLATE NOCASE IN ({matches}))"
                : $"EXISTS (SELECT 1 FROM blocks b WHERE b.id IN (candidate.old_block_id, candidate.new_block_id) AND b.code COLLATE NOCASE IN ({matches}))";
            if (container)
            {
                var storage = $"SELECT 1 FROM json_each({parameter}) WHERE candidate.container_label COLLATE NOCASE IN " +
                    "(value, value || '-east', value || '-west', value || '-north', value || '-south') " +
                    "OR value COLLATE NOCASE = (SELECT code FROM inventory_types WHERE id = candidate.inventory_type_id)";
                predicate = $"({predicate} OR EXISTS ({storage}))";
            }
            conditions.Add(exclude ? $"NOT ({predicate})" : predicate);
        }
    }
}
