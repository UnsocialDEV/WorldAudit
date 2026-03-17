using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Mod;

namespace WorldAudit.Tests;

public sealed class SqliteAuditRepositoryTests : IAsyncLifetime
{
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
