using Microsoft.Data.Sqlite;
using WorldAudit.Application;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Mod;

namespace WorldAudit.Tests;

public sealed class Phase7PerformanceTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private WorldAuditRuntime _runtime = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"worldaudit-phase7-{Guid.NewGuid():N}.db");
        _runtime = await CreateRuntimeAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Migration6_CreatesLookupIndexes()
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        Assert.Contains("ix_block_events_actor_lookup", indexes);
        Assert.Contains("ix_block_events_cause_lookup", indexes);
        Assert.Contains("ix_block_events_action_lookup", indexes);
        Assert.Contains("ix_container_transactions_actor_lookup", indexes);
        Assert.Contains("ix_container_transactions_cause_lookup", indexes);
        Assert.DoesNotContain("ix_block_events_actor_time", indexes);
        Assert.DoesNotContain("ix_block_events_cause_time", indexes);
        Assert.DoesNotContain("ix_container_transactions_actor_time", indexes);
    }

    [Fact]
    public async Task Lookup_ReturnsEmptyWhenActorCauseOrActionFilterDoesNotResolve()
    {
        var center = new BlockPosition(10, 64, 10);
        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            center,
            new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:stone"));
        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var baseFilters = new LookupFilters(
            TimeRange: null,
            Radius: 3,
            ActorName: null,
            Cause: null,
            Action: null,
            PageSize: 10);

        var missingActor = await _runtime.BlockQueries.LookupAsync(
            "main",
            center,
            baseFilters with { ActorName = "Missing" }).WaitAsync(TimeSpan.FromSeconds(5));
        var missingCause = await _runtime.BlockQueries.LookupAsync(
            "main",
            center,
            baseFilters with { Cause = AuditCause.System }).WaitAsync(TimeSpan.FromSeconds(5));
        var missingAction = await _runtime.BlockQueries.LookupAsync(
            "main",
            center,
            baseFilters with { Action = BlockAuditAction.Break }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(missingActor);
        Assert.Empty(missingCause);
        Assert.Empty(missingAction);
    }

    [Fact]
    public async Task Maintenance_UsesPassiveCheckpointForWalThreshold_AndOptimizeKeepsOwnCadence()
    {
        var start = new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero);
        await _runtime.ProcessMaintenanceTickAsync(force: true, now: start).WaitAsync(TimeSpan.FromSeconds(5));
        var initialStatus = await _runtime.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var initialOptimizeAt = initialStatus.LastOptimize?.CompletedAt;

        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using (var walStream = new FileStream(
            $"{_databasePath}-wal",
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite))
        {
            walStream.SetLength(2 * 1024 * 1024);
        }

        await _runtime.ProcessMaintenanceTickAsync(force: false, now: start.AddMinutes(1)).WaitAsync(TimeSpan.FromSeconds(5));
        var status = await _runtime.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(status.LastCheckpoint);
        Assert.Equal(SqliteCheckpointMode.Passive, status.LastCheckpoint!.Mode);
        Assert.Equal("wal threshold", status.LastCheckpoint.Reason);
        Assert.Equal(initialOptimizeAt, status.LastOptimize?.CompletedAt);
    }

    [Fact]
    public async Task Status_ReportsPhase7PerformanceFields()
    {
        var position = new BlockPosition(20, 64, 20);
        await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
            "main",
            position,
            new DateTimeOffset(2026, 3, 17, 13, 0, 0, TimeSpan.Zero),
            "Dayton",
            "player-1",
            "game:granite"));
        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await _runtime.BlockQueries.GetHistoryAsync("main", position).WaitAsync(TimeSpan.FromSeconds(5));
        await _runtime.ProcessMaintenanceTickAsync(force: true, now: new DateTimeOffset(2026, 3, 17, 13, 5, 0, TimeSpan.Zero)).WaitAsync(TimeSpan.FromSeconds(5));

        var status = await _runtime.GetStatusAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(status.QueryPerformance.QueryCount > 0);
        Assert.NotNull(status.LastCheckpoint);
        Assert.NotNull(status.LastOptimize);
        Assert.Equal(TimeSpan.FromMinutes(30), status.CheckpointPolicy.CheckpointInterval);
        Assert.Equal(1L * 1024L * 1024L, status.CheckpointPolicy.WalSizeThresholdBytes);
        Assert.Equal(TimeSpan.FromHours(24), status.CheckpointPolicy.OptimizeInterval);
        Assert.True(status.WriterSnapshot.LargestFlushBatchSize > 0);
    }

    private Task<WorldAuditRuntime> CreateRuntimeAsync()
    {
        return WorldAuditRuntime.CreateAsync(new WorldAuditOptions
        {
            DatabasePath = _databasePath,
            WriterBatchSize = 2,
            WriterMaxFlushDelay = TimeSpan.FromMilliseconds(25),
            DefaultPageSize = 8,
            CheckpointIntervalMinutes = 30,
            CheckpointWalSizeMegabytes = 1,
            OptimizeIntervalHours = 24,
            SlowQueryThresholdMilliseconds = 1,
            SlowFlushThresholdMilliseconds = 1
        });
    }
}
