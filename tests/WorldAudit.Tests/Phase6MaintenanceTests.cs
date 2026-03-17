using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Mod;

namespace WorldAudit.Tests;

public sealed class Phase6MaintenanceTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private WorldAuditRuntime _runtime = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"worldaudit-phase6-{Guid.NewGuid():N}.db");
        _runtime = await CreateRuntimeAsync(retentionDays: 0).WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Status_ReportsRetentionAndMaintenance()
    {
        await _runtime.ProcessMaintenanceTickAsync(force: true, now: new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero))
            .WaitAsync(TimeSpan.FromSeconds(5));

        var status = await _runtime.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(_databasePath, status.DatabasePath);
        Assert.False(string.IsNullOrWhiteSpace(status.SqliteVersion));
        Assert.NotNull(status.LastMaintenanceRun);
        Assert.True(status.LastMaintenanceRun!.Succeeded);
        Assert.Equal(0, status.RetentionDays);
    }

    [Fact]
    public async Task PauseStopsPersistenceUntilResume()
    {
        var position = new BlockPosition(20, 64, 20);
        _runtime.PauseConsumer();

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            position,
            new DateTimeOffset(2026, 3, 16, 10, 0, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:stone"));

        await Task.Delay(150);
        var beforeResume = await _runtime.BlockQueries.GetHistoryAsync("main", position).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(beforeResume);
        Assert.True(_runtime.AuditWriter.Snapshot().IsPaused);

        _runtime.ResumeConsumer();
        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var afterResume = await _runtime.BlockQueries.GetHistoryAsync("main", position).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(afterResume);
    }

    [Fact]
    public async Task FlushPersistsWhilePaused()
    {
        var position = new BlockPosition(21, 64, 21);
        _runtime.PauseConsumer();

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            position,
            new DateTimeOffset(2026, 3, 16, 10, 5, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:granite"));

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var history = await _runtime.BlockQueries.GetHistoryAsync("main", position).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(history);
        Assert.True(_runtime.AuditWriter.Snapshot().IsPaused);

        _runtime.ResumeConsumer();
    }

    [Fact]
    public async Task PurgePreview_DoesNotDeleteOldRows()
    {
        var oldTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var position = new BlockPosition(22, 64, 22);
        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            position,
            oldTime,
            "Dayton",
            "player-1",
            "game:basalt"));
        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var preview = await _runtime.PreviewPurgeAsync(TimeSpan.FromDays(30), new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var history = await _runtime.BlockQueries.GetHistoryAsync("main", position).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, preview.BlockEventCount);
        Assert.Single(history);
    }

    [Fact]
    public async Task PurgeExecute_DeletesOldAuditRowsAndTerminalRollbackJobs()
    {
        var oldTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var recentTime = new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero);
        var oldBlockPosition = new BlockPosition(23, 64, 23);
        var recentBlockPosition = new BlockPosition(24, 64, 24);
        var containerPosition = new BlockPosition(25, 64, 25);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            oldBlockPosition,
            oldTime,
            "Dayton",
            "player-1",
            "game:limestone"));
        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            recentBlockPosition,
            recentTime,
            "Dayton",
            "player-1",
            "game:slate"));
        await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
            WorldId: "main",
            Position: containerPosition,
            OccurredAt: oldTime,
            ActorName: "Dayton",
            ActorExternalId: "player-1",
            Cause: AuditCause.Player,
            InventoryType: "chest",
            ContainerLabel: "game:chest",
            Lines:
            [
                new ContainerTransactionLineCapture("game:torch", 0, 4, 0, 4)
            ]));
        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var job = await CreatePreviewJobAsync(oldBlockPosition, oldTime, action: BlockAuditAction.Place).WaitAsync(TimeSpan.FromSeconds(5));
        await _runtime.Repository.UpdateRollbackJobStateAsync(
            job.Id,
            RollbackJobState.Completed,
            completedAt: oldTime).WaitAsync(TimeSpan.FromSeconds(5));

        var purgeResult = await _runtime.ExecutePurgeAsync(
            TimeSpan.FromDays(30),
            new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, purgeResult.DeletedBlockEventCount);
        Assert.Equal(1, purgeResult.DeletedContainerTransactionCount);
        Assert.Equal(1, purgeResult.DeletedRollbackJobCount);
        Assert.Empty(await _runtime.BlockQueries.GetHistoryAsync("main", oldBlockPosition).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(await _runtime.BlockQueries.GetHistoryAsync("main", recentBlockPosition).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(await _runtime.ContainerQueries.GetHistoryAsync("main", containerPosition).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await _runtime.Repository.GetRollbackJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PurgeExecute_BlocksWhenPreviewedRollbackJobExists()
    {
        var oldTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var position = new BlockPosition(26, 64, 26);

        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            position,
            oldTime,
            "Dayton",
            "player-1",
            "game:andesite"));
        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await CreatePreviewJobAsync(position, oldTime, action: BlockAuditAction.Place).WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _runtime.ExecutePurgeAsync(
            TimeSpan.FromDays(30),
            new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task ScheduledMaintenance_SkipsWhenBlockingRollbackJobsExist()
    {
        await _runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        _runtime = await CreateRuntimeAsync(retentionDays: 30).WaitAsync(TimeSpan.FromSeconds(5));

        var oldTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var position = new BlockPosition(27, 64, 27);
        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            position,
            oldTime,
            "Dayton",
            "player-1",
            "game:chalk"));
        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await CreatePreviewJobAsync(position, oldTime, action: BlockAuditAction.Place).WaitAsync(TimeSpan.FromSeconds(5));

        var result = await _runtime.ProcessMaintenanceTickAsync(
            force: true,
            now: new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Succeeded);
        Assert.Contains("skipped", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<WorldAuditRuntime> CreateRuntimeAsync(int retentionDays)
    {
        return await WorldAuditRuntime.CreateAsync(new WorldAuditOptions
        {
            DatabasePath = _databasePath,
            WriterBatchSize = 2,
            WriterMaxFlushDelay = TimeSpan.FromMilliseconds(25),
            DefaultPageSize = 8,
            RollbackPreviewLimit = 100,
            RetentionDays = retentionDays
        });
    }

    private async Task<RollbackJob> CreatePreviewJobAsync(BlockPosition center, DateTimeOffset occurredAt, BlockAuditAction action)
    {
        var filters = new LookupFilters(
            new AuditTimeRange(occurredAt.AddMinutes(-1), occurredAt.AddMinutes(1)),
            Radius: 5,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: action,
            PageSize: null,
            IncludeBlocks: true,
            IncludeContainers: false);

        return await _runtime.RollbackPlanner.PreviewRollbackAsync(
            "main",
            center,
            filters,
            new AuditActor("Admin", "admin-1"),
            "t:2m r:5 u:Dayton a:place -b");
    }
}
