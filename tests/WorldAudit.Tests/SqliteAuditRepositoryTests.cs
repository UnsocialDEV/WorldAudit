using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Mod;

namespace WorldAudit.Tests;

public sealed class SqliteAuditRepositoryTests : IAsyncLifetime
{
    [Fact]
    public async Task AreaRollbackOmitsJobHistoryBeforeEachLimitAndHonorsExplicitCauses()
    {
        var pos = new BlockPosition(2, 64, 3);
        var now = DateTimeOffset.UtcNow;
        var block = new BlockAuditEvent(null, "main", pos, now, new("Admin"), AuditCause.Player,
            BlockAuditAction.Break, "game:planks", "game:air");
        var container = new ContainerAuditTransaction(null, "main", pos, now, new("Admin"), AuditCause.Player,
            "chest", null, null, null, []);
        var metadata = new (AuditCause Cause, long? Job)[] {
            (AuditCause.Player, null), (AuditCause.Rollback, null), (AuditCause.Restore, null), (AuditCause.Player, 12) };
        await _runtime.Repository.WriteBlockEventsAsync(metadata.Select((m, i) => block with {
            Cause = m.Cause, SourceJobId = m.Job, OccurredAt = now.AddSeconds(i) }).ToArray());
        await _runtime.Repository.WriteContainerTransactionsAsync(metadata.Select((m, i) => container with {
            Cause = m.Cause, SourceJobId = m.Job, OccurredAt = now.AddSeconds(i) }).ToArray());
        var blocks = new RollbackBlockSelectionQuery("main", pos, 5, Limit: 1);
        var containers = new ContainerRollbackSelectionQuery("main", pos, 5, Limit: 1);
        Assert.Equal(1L, Assert.Single(await _runtime.Repository.SelectRollbackBlockEventsAsync(blocks)).Id);
        Assert.Equal(1L, Assert.Single(await _runtime.Repository.SelectRollbackContainerTransactionsAsync(containers)).Id);
        foreach (var cause in new[] { AuditCause.Rollback, AuditCause.Restore })
        {
            var filters = new LookupCommandParser().Parse([$"u:#{cause}"], now);
            var job = await _runtime.RollbackPlanner.PreviewRollbackAsync("main", pos, filters, new("Admin"), cause.ToString());
            Assert.Equal(1, job.PlannedBlockCount);
            Assert.Equal(1, job.PlannedContainerCount);
        }
        var broad = new LookupCommandParser().Parse(["r:5"], now);
        var restore = await _runtime.RollbackPlanner.PreviewRestoreAsync("main", pos, broad, new("Admin"), "restore");
        Assert.Equal(4, restore.PlannedBlockCount);
        Assert.Equal(4, restore.PlannedContainerCount);
        Assert.Equal(4, (await _runtime.BlockQueries.GetHistoryAsync("main", pos)).Count);
        Assert.Equal(4, (await _runtime.ContainerQueries.LookupAsync("main", pos, broad)).Count);
    }

    [Fact]
    public async Task RollbackLimitCountsMaterialDamageWhileRestoreAndInspectIncludeFlames()
    {
        var pos = new BlockPosition(10, 65, 10);
        var now = DateTimeOffset.UtcNow;
        var damage = new BlockAuditEvent(null, "main", pos, now, new("Alice"), AuditCause.Fire,
            BlockAuditAction.Replace, "game:log-grown-oak-ud", "game:fire");
        await _runtime.Repository.WriteBlockEventsAsync([
            damage,
            damage with { OccurredAt = now.AddSeconds(1), OldBlockCode = "game:fire", NewBlockCode = "game:air" },
            damage with { OccurredAt = now.AddSeconds(2), OldBlockCode = null, NewBlockCode = "game:fire" }
        ]);
        var selection = new RollbackBlockSelectionQuery("main", pos, 5, Limit: 1);
        Assert.Equal(damage.OldBlockCode, Assert.Single(await _runtime.Repository.SelectRollbackBlockEventsAsync(selection)).OldBlockCode);
        var filters = new LookupFilters(null, 5, "Alice", null, null, null, IncludeContainers: false);
        var restore = await _runtime.RollbackPlanner.PreviewRestoreAsync("main", pos, filters, new("Admin"), "restore");
        Assert.Equal(3, restore.PlannedBlockCount);
        Assert.Equal(3, (await _runtime.BlockQueries.GetHistoryAsync("main", pos)).Count);
    }

    [Fact]
    public async Task FireTicksNeitherEnterTheQueueNorCrowdOutOlderDamageInExistingHistory()
    {
        var pos = new BlockPosition(10, 65, 10);
        var now = DateTimeOffset.UtcNow;
        var burn = new BlockAuditEvent(null, "main", pos, now, new("Alice"), AuditCause.Fire,
            BlockAuditAction.Break, "game:log-grown-oak-ud", "game:air");
        var tick = burn with { OccurredAt = now.AddSeconds(1), Action = BlockAuditAction.Replace,
            OldBlockCode = "game:fire", NewBlockCode = "game:fire", OldBlockEntitySnapshot = [1], NewBlockEntitySnapshot = [2] };
        _runtime.AuditWriter.QueueBlockEvent(tick);
        var firepit = tick with { OldBlockCode = "game:firepit-lit", NewBlockCode = "game:firepit-lit" };
        _runtime.AuditWriter.QueueBlockEvent(firepit);
        _runtime.AuditWriter.QueueBlockEvent(burn);
        await _runtime.FlushAsync();
        Assert.Equal(2, _runtime.AuditWriter.Snapshot().PersistedEvents);

        // Seed the rows an older release would already have persisted, exceeding the default preview limit.
        await _runtime.Repository.WriteBlockEventsAsync(Enumerable.Repeat(tick, 5001).ToArray());
        var history = await _runtime.Repository.GetBlockHistoryAsync(new("main", pos, Limit: 2));
        var lookup = await _runtime.Repository.LookupBlockEventsAsync(new("main", pos, 5, Limit: 2));
        Assert.Equal(new[] { firepit.OldBlockCode, burn.OldBlockCode }, history.Select(e => e.OldBlockCode));
        Assert.Equal(history.Select(e => e.Id), lookup.Select(e => e.Id));
        var filters = new LookupCommandParser().Parse(["u:Alice", "-b"], now);
        var job = await _runtime.RollbackPlanner.PreviewRollbackAsync("main", pos, filters, new("Admin"), "fire damage");
        Assert.Equal(2, job.PlannedBlockCount);
        var plan = await _runtime.Repository.GetRollbackBlockPlanEntriesAsync(job.Id);
        Assert.Equal(history.Select(e => e.Id!.Value), plan.Select(e => e.SourceEventId));
    }

    [Fact]
    public async Task QueriesAndRollbackSelectionSeparateDimensionsAndSavegames()
    {
        var center = new BlockPosition(10, 65, 10);
        var otherDimension = new BlockPosition(10, 32768 + 65, 10);
        foreach (var (world, position) in new[] { ("save-a", center), ("save-a", otherDimension), ("save-b", center) })
            await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(world, position,
                DateTimeOffset.UtcNow, "Alice", "alice", "game:dirt"));
        await _runtime.FlushAsync();
        var history = await _runtime.Repository.GetBlockHistoryAsync(new BlockHistoryQuery("save-a", otherDimension));
        Assert.Equal(otherDimension, Assert.Single(history).Position);
        var lookup = await _runtime.Repository.LookupBlockEventsAsync(new BlockLookupQuery("save-a", center, 40000));
        Assert.Equal(center, Assert.Single(lookup).Position);
        var plan = await _runtime.Repository.SelectRollbackBlockEventsAsync(new RollbackBlockSelectionQuery("save-a", center, 40000));
        Assert.Equal(center, Assert.Single(plan).Position);
    }

    [Fact]
    public async Task BoundedQueriesReadOnlyRequestedSnapshotsAfterRadiusAndCodeFilters()
    {
        var center = new BlockPosition(0, 0, 0);
        var now = DateTimeOffset.UtcNow;
        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerBroke("main", center, now,
            "Alice", "alice", "game:stone"));
        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext("main", center, now,
            "Alice", "alice", AuditCause.Player, "container", "game:chest-east",
            [new ContainerTransactionLineCapture("game:rock", 0, -1, 1, 0)]));
        await _runtime.FlushAsync();
        await using var connection = await new WorldAudit.Infrastructure.Persistence.SqliteConnectionFactory(_runtime.Options).OpenConnectionAsync();
        using var seed = connection.CreateCommand();
        // Non-blob sentinels make accidental snapshot materialization outside the page fail the test.
        seed.CommandText = """
            WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM numbers WHERE n<33000)
            INSERT INTO container_transactions(world_id,x,y,z,chunk_x,chunk_y,chunk_z,occurred_at_ms,actor_id,cause_id,
                inventory_type_id,container_label,before_snapshot_blob)
            SELECT world_id,4,4,4,0,0,0,occurred_at_ms+n,actor_id,cause_id,inventory_type_id,container_label,'outside'
            FROM container_transactions, numbers WHERE id=1;
            INSERT INTO block_events(world_id,x,y,z,chunk_x,chunk_y,chunk_z,occurred_at_ms,actor_id,cause_id,action_id,
                old_block_id,old_block_entity_blob)
            SELECT world_id,0,0,0,0,0,0,occurred_at_ms-1,actor_id,cause_id,action_id,old_block_id,'outside page'
            FROM block_events WHERE id=1;
            """;
        await seed.ExecuteNonQueryAsync();
        var filters = new LookupCommandParser().Parse(["r:5", "p:1"], now);
        Assert.Equal(1L, Assert.Single(await _runtime.BlockQueries.LookupAsync("main", center, filters)).Id);
        Assert.Equal(1L, Assert.Single(await _runtime.ContainerQueries.LookupAsync("main", center, filters)).Id);
        Assert.Equal(1L, Assert.Single(await _runtime.Repository.GetBlockHistoryAsync(
            new BlockHistoryQuery("main", center, IncludeCodes: ["game:stone"], Limit: 1))).Id);
        Assert.Equal(1L, Assert.Single(await _runtime.Repository.GetContainerHistoryAsync(
            new ContainerHistoryQuery("main", center, IncludeCodes: ["game:chest"], Limit: 1))).Id);
        Assert.Equal(1L, Assert.Single(await _runtime.Repository.SelectRollbackBlockEventsAsync(
            new RollbackBlockSelectionQuery("main", center, 5, Limit: 1))).Id);
        Assert.Equal(1L, Assert.Single(await _runtime.Repository.SelectRollbackContainerTransactionsAsync(
            new ContainerRollbackSelectionQuery("main", center, 5, Limit: 1))).Id);
        foreach (var code in new[] { "game:chest", "game:rock" })
        {
            var query = filters with { IncludeCodes = [code] };
            Assert.Single(await _runtime.ContainerQueries.LookupAsync("main", center, query));
            var preview = await _runtime.RollbackPlanner.PreviewRollbackAsync("main", center, query, new AuditActor("Admin"), code);
            Assert.Equal(1, preview.PlannedContainerCount);
        }
        var breaks = filters with { Action = BlockAuditAction.Break, IncludeCodes = ["game:stone"] };
        Assert.Empty(await _runtime.ContainerQueries.LookupAsync("main", center, breaks));
        // Remove the deliberately unreadable older block before requesting a multi-row preview.
        seed.CommandText = "DELETE FROM block_events WHERE id > 1;";
        await seed.ExecuteNonQueryAsync();
        var job = await _runtime.RollbackPlanner.PreviewRollbackAsync("main", center, breaks, new AuditActor("Admin"), "a:break");
        Assert.Equal(1, job.PlannedBlockCount);
        Assert.Equal(0, job.PlannedContainerCount);

        // Exercise detail hydration beyond SQLite's usual 32,766-variable limit.
        seed.CommandText = "UPDATE container_transactions SET before_snapshot_blob = NULL WHERE id > 1;";
        await seed.ExecuteNonQueryAsync();
        var many = await _runtime.Repository.LookupContainerTransactionsAsync(new ContainerLookupQuery("main", center, 10, Limit: 33001));
        Assert.Equal(33001, many.Count);
        Assert.Equal("game:rock", Assert.Single(many[^1].Lines).ItemCode);
    }

    [Fact]
    public async Task InspectReadsCompleteVisitsAndPagesTheirOriginalTransactions()
    {
        var pos = new BlockPosition(1, 65, 2);
        var first = DateTimeOffset.FromUnixTimeMilliseconds(1000);
        var item = new ContainerAuditLine(null, "game:copper", 4, -3, 5, 2);
        var take = new ContainerAuditTransaction(null, "main", pos, first, new("Alice", "uid-a"),
            AuditCause.Player, "chest", "game:chest", [1], [2], [item], VisitId: "first");
        var deposits = take with { Lines = [item with { QuantityDelta = 2, BeforeQuantity = 2, AfterQuantity = 4 }] };
        var rows = Enumerable.Range(0, 61).Select(i => (i == 60 ? deposits : take) with {
            OccurredAt = first.AddSeconds(i * 10) }).ToList();
        rows.Add(take with { Actor = new("Bob", "uid-b"), VisitId = "bob", OccurredAt = first.AddSeconds(30) });
        rows.Add(take with { VisitId = "reopened", OccurredAt = rows[60].OccurredAt.AddMilliseconds(1) });
        await _runtime.Repository.WriteContainerTransactionsAsync(rows);

        var entries = await _runtime.ContainerInspectHistory.ReadAsync("main", pos, 2);
        Assert.Equal(new[] { "reopened", "first" }, entries.Select(e => e.VisitId));
        var visit = entries[1];
        Assert.Equal(61, visit.Transactions);
        Assert.Equal(new long[] { -180, 2 }, visit.Items.Select(i => i.Delta));
        var summary = WorldAudit.Presentation.Chat.ContainerInspectSummary.Format(visit, first);
        Assert.Contains("Removed 180 Copper", summary.What);
        Assert.Contains("Added 2 Copper", summary.What);
        var detail = Assert.Single(await _runtime.ContainerInspectDetails.ReadAsync("main", visit.DetailId, 21, 3));
        Assert.Equal(rows[60].OccurredAt, detail.OccurredAt);
        Assert.Equal(4, Assert.Single(detail.Lines).SlotId);
        Assert.Equal(new byte[] { 1 }, detail.BeforeSnapshot);
        Assert.Empty(await _runtime.ContainerInspectDetails.ReadAsync("other-world", visit.DetailId, 1, 3));
        Assert.Equal(63, (await _runtime.Repository.SelectRollbackContainerTransactionsAsync(
            new("main", pos, 0, Limit: 100))).Count);
    }

    [Fact]
    public async Task UpgradeGroupsOnlyConsecutiveLegacyPlayerChangesWithinFiveSeconds()
    {
        var pos = new BlockPosition(1, 65, 2);
        var first = DateTimeOffset.FromUnixTimeMilliseconds(1000);
        var row = new ContainerAuditTransaction(null, "main", pos, first, new("Alice", "uid"),
            AuditCause.Player, "chest", null, null, null, []);
        var times = new[] { 0, 5000, 10000, 15001, 15002, 15003, 15004, 15006, 15007, 15008, 15009 };
        var old = times.Select(ms => row with { OccurredAt = first.AddMilliseconds(ms) }).ToArray();
        old[4] = old[4] with { Actor = new("Bob") };
        old[8] = old[8] with { Cause = AuditCause.Unknown };
        old[9] = old[9] with { Cause = AuditCause.Unknown };
        await _runtime.Repository.WriteContainerTransactionsAsync(old);
        await _runtime.Repository.WriteBlockEventsAsync([new(null, "main", pos, first.AddMilliseconds(15005),
            new("Bob"), AuditCause.Player, BlockAuditAction.Break, "game:chest", "game:air")]);
        await using (var connection = await new WorldAudit.Infrastructure.Persistence.SqliteConnectionFactory(_runtime.Options).OpenConnectionAsync())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DROP TABLE container_visit_members; DELETE FROM schema_migrations WHERE version = 10;";
            await cmd.ExecuteNonQueryAsync();
        }
        await _runtime.SchemaMigrator.MigrateAsync();
        await _runtime.SchemaMigrator.MigrateAsync();
        await _runtime.Repository.WriteContainerTransactionsAsync([row with { OccurredAt = first.AddMilliseconds(15010) }]);
        var stored = (await _runtime.ContainerQueries.GetHistoryAsync("main", pos, limit: 50)).OrderBy(t => t.Id).ToArray();
        Assert.Equal(new string?[] { "legacy:1", "legacy:1", "legacy:1", null, null, "legacy:6", "legacy:6",
            null, null, null, null, null }, stored.Select(t => t.VisitId));
        var grouped = (await _runtime.ContainerInspectHistory.ReadAsync("main", pos, 50)).Single(v => v.Transactions == 3);
        Assert.Contains("grouped activity", WorldAudit.Presentation.Chat.ContainerInspectSummary.Format(grouped, first).Type);
        Assert.Equal(3, (await _runtime.ContainerInspectDetails.ReadAsync("main", grouped.DetailId, 1, 3)).Count);
    }

    [Fact]
    public async Task FrozenPreviewPagesIncludeAllMatchesAndOnlySelectedMembersOfAVisit()
    {
        var position = new BlockPosition(1, 64, 1);
        var actor = new AuditActor("Alice", "uid");
        var first = DateTimeOffset.FromUnixTimeMilliseconds(100000);
        var block = new BlockAuditEvent(null, "main", position, first, actor, AuditCause.Player,
            BlockAuditAction.Place, "game:air", "game:stone");
        await _runtime.Repository.WriteBlockEventsAsync(Enumerable.Range(0, 40)
            .Select(i => block with { OccurredAt = first.AddMilliseconds(i) }).ToArray());
        var container = new ContainerAuditTransaction(null, "main", position, first, actor, AuditCause.Player,
            "chest", null, null, null, [new(null, "game:copper", 1, -2, 2, 0)], VisitId: "long-visit");
        await _runtime.Repository.WriteContainerTransactionsAsync([container]);
        await using var connection = await new WorldAudit.Infrastructure.Persistence.SqliteConnectionFactory(_runtime.Options).OpenConnectionAsync();
        using var seed = connection.CreateCommand();
        seed.CommandText = """
            WITH RECURSIVE seq(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM seq WHERE n<19999)
            INSERT INTO container_transactions(world_id,x,y,z,chunk_x,chunk_y,chunk_z,occurred_at_ms,actor_id,cause_id,inventory_type_id)
            SELECT world_id,x,y,z,chunk_x,chunk_y,chunk_z,occurred_at_ms+n,actor_id,cause_id,inventory_type_id
            FROM seq CROSS JOIN container_transactions WHERE id=1;
            INSERT INTO container_visit_members SELECT id,'long-visit' FROM container_transactions WHERE id>1;
            INSERT INTO container_transaction_lines(transaction_id,item_id,slot_id,quantity_delta,before_quantity,after_quantity)
            SELECT ct.id, l.item_id,1,-2,2,0 FROM container_transactions ct JOIN container_transaction_lines l ON l.transaction_id=1
            WHERE ct.id>1;
            """;
        await seed.ExecuteNonQueryAsync();
        var planner = new RollbackPlanner(_runtime.Repository, new() { RollbackPreviewLimit = 32 });
        var filter = new LookupFilters(new(first, first.AddMilliseconds(19999)), 0, null, null, null, null);
        var job = await planner.PreviewRollbackAsync("main", position, filter, actor, "large visit");
        Assert.Equal((40, 1, 20000), (job.PlannedBlockCount, job.PlannedVisitCount, job.PlannedContainerCount));
        // Same time, position and visit as the frozen records, but committed after the preview.
        await _runtime.Repository.WriteContainerTransactionsAsync([container]);
        await _runtime.Repository.WriteBlockEventsAsync([block]);
        var storage = Assert.Single(await _runtime.ContainerInspectHistory.ReadAsync("main", null, 8, job.Id));
        Assert.Equal(20000, storage.Transactions);
        Assert.Equal(-40000, Assert.Single(storage.Items).Delta);
        Assert.Empty(await _runtime.ContainerInspectHistory.ReadAsync("main", null, 8, job.Id, 2));
        var last = await _runtime.JobVisitDetails.ReadAsync(job.Id, "main", storage.DetailId, 2500, 8);
        Assert.Equal(20000, last.Count);
        Assert.Equal(Enumerable.Range(1, 8).Reverse().Select(i => (long?)i), last.Rows.Select(row => row.Id));
        Assert.Equal(0, (await _runtime.JobVisitDetails.ReadAsync(job.Id, "main", 20001, 1, 8)).Count);
        var blocks = await _runtime.JobBlockPage.ReadAsync(job.Id, "main", 5, 8);
        Assert.Equal(Enumerable.Range(1, 8).Reverse().Select(i => (long?)i), blocks.Select(row => row.Id));
        var partial = await planner.PreviewRollbackAsync("main", position, filter with {
            IncludeBlocks = false, TimeRange = new(first.AddMilliseconds(7), first.AddMilliseconds(9)) }, actor, "partial");
        var entry = Assert.Single(await _runtime.ContainerInspectHistory.ReadAsync("main", null, 8, partial.Id));
        Assert.Equal((0, 1, 3), (partial.PlannedBlockCount, partial.PlannedVisitCount, partial.PlannedContainerCount));
        Assert.Equal(-6, Assert.Single(entry.Items).Delta);
        Assert.Equal(new long?[] { 10, 9, 8 }, (await _runtime.JobVisitDetails.ReadAsync(partial.Id, "main", entry.DetailId, 1, 8)).Rows.Select(t => t.Id));
        Assert.Equal(0, (await _runtime.JobVisitDetails.ReadAsync(partial.Id, "main", storage.DetailId, 1, 8)).Count);
    }

    [Fact]
    public async Task UpgradeCountsVisitsOfExistingJobsWithoutExpandingTheirSelection()
    {
        var pos = new BlockPosition(1, 64, 1);
        var actor = new AuditActor("Alice");
        var row = new ContainerAuditTransaction(null, "main", pos, DateTimeOffset.UnixEpoch, actor,
            AuditCause.Player, "trunk", null, null, null, [], VisitId: "legacy:1");
        await _runtime.Repository.WriteContainerTransactionsAsync([row, row, row, row with { VisitId = null }]);
        var job = await _runtime.Repository.CreateRollbackJobAsync(new("main", actor, RollbackJobOperation.Rollback,
            "old capped preview", RollbackJobState.Previewed, 0, 3, 1, row.OccurredAt),
            [new(RollbackJobEntryTargetType.Container, 1, 0), new(RollbackJobEntryTargetType.Container, 2, 1),
             new(RollbackJobEntryTargetType.Container, 4, 2)]);
        using var connection = await new WorldAudit.Infrastructure.Persistence.SqliteConnectionFactory(_runtime.Options).OpenConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX ix_job_entries_target;
            ALTER TABLE rollback_jobs DROP COLUMN planned_visit_count;
            DELETE FROM schema_migrations WHERE version=11;
            """;
        await command.ExecuteNonQueryAsync();
        await _runtime.SchemaMigrator.MigrateAsync();
        var oldJob = await _runtime.Repository.GetRollbackJobAsync(job.Id);
        Assert.Equal((2, 3), (oldJob!.PlannedVisitCount, oldJob.PlannedContainerCount));
        var entries = await _runtime.ContainerInspectHistory.ReadAsync("main", null, 1, job.Id, 2);
        var grouped = Assert.Single(entries);
        Assert.Equal("legacy:1", grouped.VisitId);
        Assert.Equal(2, grouped.Transactions);
        Assert.Equal(0, (await _runtime.JobVisitDetails.ReadAsync(job.Id, "main", 3, 1, 8)).Count);
    }

    private string _databasePath = null!;
    private WorldAuditRuntime _runtime = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"worldaudit-{Guid.NewGuid():N}.db");
        _runtime = await WorldAuditRuntime.CreateAsync(new WorldAuditOptions
        {
            DatabasePath = _databasePath,
            WriterBatchSize = 2,
            WriterMaxFlushDelay = TimeSpan.FromMilliseconds(25),
            DefaultPageSize = 8
        }).WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Runtime dispose timed out. Snapshot: {_runtime.AuditWriter.Snapshot()}");
        }
    }

    [Fact]
    public async Task Inspect_ReturnsNewestFirstForExactLocation()
    {
        var position = new BlockPosition(10, 65, -4);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            position,
            new DateTimeOffset(2026, 3, 16, 10, 0, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:oakplanks"));

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerBroke(
            "main",
            position,
            new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero),
            "Rowan",
            "player-2",
            "game:oakplanks"));

        try
        {
            await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Flush timed out. Snapshot: {_runtime.AuditWriter.Snapshot()}");
        }

        var history = await _runtime.BlockQueries.GetHistoryAsync("main", position).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, history.Count);
        Assert.Equal("Rowan", history[0].Actor.Name);
        Assert.Equal(BlockAuditAction.Break, history[0].Action);
        Assert.Equal("Dayton", history[1].Actor.Name);
    }

    [Fact]
    public async Task Lookup_FiltersByRadiusAndTime()
    {
        var center = new BlockPosition(0, 64, 0);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            new BlockPosition(1, 64, 1),
            new DateTimeOffset(2026, 3, 16, 11, 50, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:stonebrick"));

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            new BlockPosition(8, 64, 8),
            new DateTimeOffset(2026, 3, 16, 11, 55, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:granite"));

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            new BlockPosition(2, 64, 2),
            new DateTimeOffset(2026, 3, 16, 9, 0, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:clay"));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var filters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero)),
            Radius: 4,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Place,
            PageSize: 10);

        var results = await _runtime.BlockQueries.LookupAsync("main", center, filters).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(results);
        Assert.Equal(new BlockPosition(1, 64, 1), results[0].Position);
    }

    [Fact]
    public async Task ContainerHistory_ReturnsNewestFirstWithLines()
    {
        var position = new BlockPosition(3, 65, 9);

        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
            WorldId: "main",
            Position: position,
            OccurredAt: new DateTimeOffset(2026, 3, 16, 10, 0, 0, TimeSpan.Zero),
            ActorName: "Dayton",
            ActorExternalId: "player-1",
            Cause: AuditCause.Player,
            InventoryType: "crate",
            ContainerLabel: "game:crate",
            Lines:
            [
                new ContainerTransactionLineCapture("game:oakplanks", 0, 8, 0, 8)
            ]));

        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
            WorldId: "main",
            Position: position,
            OccurredAt: new DateTimeOffset(2026, 3, 16, 11, 0, 0, TimeSpan.Zero),
            ActorName: "Rowan",
            ActorExternalId: "player-2",
            Cause: AuditCause.Player,
            InventoryType: "crate",
            ContainerLabel: "game:crate",
            Lines:
            [
                new ContainerTransactionLineCapture("game:oakplanks", 0, -4, 8, 4)
            ]));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var history = await _runtime.ContainerQueries.GetHistoryAsync("main", position).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, history.Count);
        Assert.Equal("Rowan", history[0].Actor.Name);
        Assert.Single(history[0].Lines);
        Assert.Equal(-4, history[0].Lines[0].QuantityDelta);
        Assert.Equal("Dayton", history[1].Actor.Name);
    }

    [Fact]
    public async Task ContainerLookup_FiltersByRadiusAndActor()
    {
        var center = new BlockPosition(0, 64, 0);

        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
            WorldId: "main",
            Position: new BlockPosition(1, 64, 1),
            OccurredAt: new DateTimeOffset(2026, 3, 16, 11, 45, 0, TimeSpan.Zero),
            ActorName: "Dayton",
            ActorExternalId: "player-1",
            Cause: AuditCause.Player,
            InventoryType: "chest",
            ContainerLabel: "game:chest",
            Lines:
            [
                new ContainerTransactionLineCapture("game:torch", 2, 4, 0, 4)
            ]));

        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
            WorldId: "main",
            Position: new BlockPosition(8, 64, 8),
            OccurredAt: new DateTimeOffset(2026, 3, 16, 11, 50, 0, TimeSpan.Zero),
            ActorName: "Other",
            ActorExternalId: "player-2",
            Cause: AuditCause.Player,
            InventoryType: "chest",
            ContainerLabel: "game:chest",
            Lines:
            [
                new ContainerTransactionLineCapture("game:torch", 2, 1, 0, 1)
            ]));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var filters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero)),
            Radius: 4,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: null,
            PageSize: 10,
            IncludeBlocks: false,
            IncludeContainers: true);

        var results = await _runtime.ContainerQueries.LookupAsync("main", center, filters).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(results);
        Assert.Equal(new BlockPosition(1, 64, 1), results[0].Position);
        Assert.Equal("Dayton", results[0].Actor.Name);
    }

    [Fact]
    public async Task BlockLookup_FiltersByIncludeAndExcludeCodes()
    {
        var center = new BlockPosition(0, 64, 0);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            new BlockPosition(1, 64, 1),
            new DateTimeOffset(2026, 3, 16, 11, 40, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:stonebrick"));

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            new BlockPosition(2, 64, 2),
            new DateTimeOffset(2026, 3, 16, 11, 45, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:granite"));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var filters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero)),
            Radius: 4,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Place,
            PageSize: 10,
            IncludeCodes: ["game:granite", "game:stonebrick"],
            ExcludeCodes: ["game:granite"]);

        var results = await _runtime.BlockQueries.LookupAsync("main", center, filters).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(results);
        Assert.Equal("game:stonebrick", results[0].NewBlockCode);
    }

    [Fact]
    public async Task ContainerLookup_FiltersByIncludeAndExcludeCodes()
    {
        var center = new BlockPosition(0, 64, 0);

        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
            WorldId: "main",
            Position: new BlockPosition(1, 64, 1),
            OccurredAt: new DateTimeOffset(2026, 3, 16, 11, 40, 0, TimeSpan.Zero),
            ActorName: "Dayton",
            ActorExternalId: "player-1",
            Cause: AuditCause.Player,
            InventoryType: "chest",
            ContainerLabel: "game:chest",
            Lines:
            [
                new ContainerTransactionLineCapture("game:torch", 0, 1, 0, 1)
            ]));

        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
            WorldId: "main",
            Position: new BlockPosition(2, 64, 2),
            OccurredAt: new DateTimeOffset(2026, 3, 16, 11, 45, 0, TimeSpan.Zero),
            ActorName: "Dayton",
            ActorExternalId: "player-1",
            Cause: AuditCause.Player,
            InventoryType: "chest",
            ContainerLabel: "game:chest",
            Lines:
            [
                new ContainerTransactionLineCapture("game:gear-temporal", 0, 1, 0, 1)
            ]));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var filters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 16, 11, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero)),
            Radius: 4,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: null,
            PageSize: 10,
            IncludeCodes: ["game:torch", "game:gear-temporal"],
            ExcludeCodes: ["game:gear-temporal"],
            IncludeBlocks: false,
            IncludeContainers: true);

        var results = await _runtime.ContainerQueries.LookupAsync("main", center, filters).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(results);
        Assert.Equal("game:torch", results[0].Lines[0].ItemCode);
    }
}
