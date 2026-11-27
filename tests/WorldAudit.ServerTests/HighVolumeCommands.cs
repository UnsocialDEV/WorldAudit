using WorldAudit.Domain;
using WorldAudit.Infrastructure.Persistence;

namespace WorldAudit.ServerTests;

public sealed partial class ServerTests
{
    private async Task HighVolumeCommands()
    {
        const int count = 33000;
        var center = Pos(570, 570);
        await runtime.FlushAsync();
        await OnServer(() =>
        {
            alice.Entityplayer.Pos.SetPos(center.X + .5, center.Y, center.Z + .5);
            AuditChatPackets.Read(alice);
        });
        await using var connection = await new SqliteConnectionFactory(runtime.Options).OpenConnectionAsync();
        using var seed = connection.CreateCommand();
        seed.CommandText = """
            WITH RECURSIVE seq(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM seq WHERE n < $count),
            sample AS (SELECT be.* FROM block_events be JOIN actors a ON a.id=be.actor_id WHERE a.name=$actor LIMIT 1)
            INSERT INTO block_events(world_id,x,y,z,chunk_x,chunk_y,chunk_z,occurred_at_ms,actor_id,cause_id,action_id,old_block_id,new_block_id)
            SELECT world_id,$x+(n%2),$y,$z,$cx,$cy,$cz,$start+2*n,actor_id,cause_id,action_id,old_block_id,new_block_id
            FROM seq CROSS JOIN sample ORDER BY n;
            SELECT last_insert_rowid();
            """;
        seed.Parameters.AddWithValue("$count", count);
        seed.Parameters.AddWithValue("$actor", alice.Player.PlayerName);
        seed.Parameters.AddWithValue("$start", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 2 * count);
        seed.Parameters.AddWithValue("$x", center.X);
        seed.Parameters.AddWithValue("$y", center.Y);
        seed.Parameters.AddWithValue("$z", center.Z);
        var chunk = AuditPos(center).ToChunkPosition(runtime.Options.ChunkSize);
        seed.Parameters.AddWithValue("$cx", chunk.X);
        seed.Parameters.AddWithValue("$cy", chunk.Y);
        seed.Parameters.AddWithValue("$cz", chunk.Z);
        var newest = Convert.ToInt64(await seed.ExecuteScalarAsync());
        Require(newest >= count, "High-volume history fixture did not populate.");
        // Every fifth container is inside the bounding box but outside the exact radius. Every third has an excluded label.
        seed.CommandText = """
            WITH RECURSIVE seq(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM seq WHERE n < $count),
            sample AS (SELECT ct.* FROM container_transactions ct JOIN actors a ON a.id=ct.actor_id WHERE a.name=$actor LIMIT 1)
            INSERT INTO container_transactions(world_id,x,y,z,chunk_x,chunk_y,chunk_z,occurred_at_ms,actor_id,cause_id,inventory_type_id,container_label)
            SELECT world_id,$x+CASE WHEN n%5=0 THEN 4 ELSE n%2 END,$y,$z+CASE WHEN n%5=0 THEN 4 ELSE 0 END,
                   $cx,$cy,$cz,$start+2*n+1,actor_id,cause_id,inventory_type_id,
                   CASE WHEN n%3=0 THEN 'game:excluded' ELSE 'game:chest-east' END FROM seq CROSS JOIN sample ORDER BY n;
            SELECT last_insert_rowid();
            """;
        var newestContainer = Convert.ToInt64(await seed.ExecuteScalarAsync());
        Require(newestContainer >= count, "High-volume container fixture did not populate.");
        seed.Parameters.Clear();
        seed.Parameters.AddWithValue("$first", newestContainer - count + 1);
        seed.Parameters.AddWithValue("$last", newestContainer);
        seed.CommandText = """
            INSERT INTO container_transaction_lines(transaction_id,item_id,slot_id,quantity_delta,before_quantity,after_quantity)
            SELECT id,(SELECT MIN(id) FROM items),0,-1,1,0 FROM container_transactions WHERE id BETWEEN $first AND $last;
            """;
        await seed.ExecuteNonQueryAsync();

        await AdminCommandKeepsTicking("/wa lookup p:1", true, () => VerifyPage(1));
        await AdminCommandKeepsTicking("/wa near", true, () => VerifyPage(runtime.Options.DefaultPageSize));
        var previous = (await runtime.Repository.GetRollbackJobsAsync(1)).First().Id;
        await AdminCommandKeepsTicking("/wa rollback r:1 e:game:excluded", true, async () =>
        {
            var job = await NewPreview(previous);
            var matchingStorage = Enumerable.Range(1, count).Count(n => n % 5 != 0 && n % 3 != 0);
            Require(job.PlannedBlockCount == count && job.PlannedContainerCount == matchingStorage,
                $"Incomplete frozen selection: {job.PlannedBlockCount} blocks, {job.PlannedContainerCount} transactions.");
            Require(job.PlannedVisitCount == matchingStorage, "Untagged changes must each have their own storage entry.");
            var first = await runtime.Repository.GetRollbackPlanBatchAsync(job.Id, 3);
            Require(first.Count == 3 && first[0] is RollbackBlockPlanEntry block && block.SourceEventId == newest,
                "The first bounded execution batch missed the newest block.");
            await OnServer(() => Command($"/wa cancel {job.Id}"));
        });

        Task VerifyPage(int size) => OnServer(() =>
        {
            var lines = AuditChatPackets.Read(alice).Select(packet => packet.Chatline?.Message).OfType<string>().ToArray();
            var rows = lines.Where(line => line.Contains(" (dimension ")).ToArray();
            Require(rows.Length == size * 2, $"Expected {size} results per history type, found {rows.Length} total.");
            var includedContainer = count;
            for (var i = 0; i < size; i++)
            {
                var pos = center.AddCopy(i % 2, 0, 0).ToLocalPosition(api);
                Require(rows[i].Contains($" at {pos.X}, {pos.Y}, {pos.Z} (dimension 0)"),
                    $"Wrong position or order in lookup row {i}: {rows[i]}");
                while (includedContainer % 5 == 0) includedContainer--;
                var storage = center.AddCopy(includedContainer % 2, 0, 0).ToLocalPosition(api);
                var label = includedContainer % 3 == 0 ? "Excluded" : "Chest East";
                Require(rows[size+i].Contains($"{label} at {storage.X}, {storage.Y}, {storage.Z} (dimension 0)"),
                    $"Wrong storage code, radius or order in lookup row {i}: {rows[size+i]}");
                includedContainer--;
            }
        });
    }
}
