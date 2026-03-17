using BenchmarkDotNet.Attributes;
using WorldAudit.Application;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Mod;

namespace WorldAudit.Benchmarks;

[MemoryDiagnoser]
public sealed class QueryBenchmarks
{
    private string _databasePath = null!;
    private WorldAuditRuntime _runtime = null!;
    private BlockPosition _exactPosition;
    private BlockPosition _center;
    private LookupFilters _lookupFilters = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"worldaudit-bench-{Guid.NewGuid():N}.db");
        _runtime = await WorldAuditRuntime.CreateAsync(new WorldAuditOptions
        {
            DatabasePath = _databasePath,
            WriterBatchSize = 256,
            WriterMaxFlushDelay = TimeSpan.FromMilliseconds(10),
            DefaultPageSize = 32
        });

        _exactPosition = new BlockPosition(64, 65, 64);
        _center = new BlockPosition(64, 65, 64);
        _lookupFilters = new LookupFilters(
            new AuditTimeRange(
                new DateTimeOffset(2026, 3, 17, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 18, 0, 0, 0, TimeSpan.Zero)),
            Radius: 8,
            ActorName: "Dayton",
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Place,
            PageSize: 64,
            IncludeBlocks: true,
            IncludeContainers: true);

        for (var index = 0; index < 512; index++)
        {
            var position = new BlockPosition(56 + (index % 16), 65, 56 + ((index / 16) % 16));
            var occurredAt = new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero).AddSeconds(index);
            await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
                "main",
                position,
                occurredAt,
                "Dayton",
                "player-1",
                $"game:block-{index % 8}"));
            await _runtime.ContainerCapture.CaptureAsync(new ContainerTransactionCaptureContext(
                "main",
                position,
                occurredAt,
                "Dayton",
                "player-1",
                AuditCause.Player,
                "crate",
                "game:crate",
                [new ContainerTransactionLineCapture("game:torch", 0, 1, index, index + 1)]));
        }

        await _runtime.FlushAsync();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
        }
    }

    [Benchmark]
    public Task<IReadOnlyList<BlockAuditEvent>> BlockHistoryExact()
    {
        return _runtime.BlockQueries.GetHistoryAsync("main", _exactPosition, limit: 32);
    }

    [Benchmark]
    public Task<IReadOnlyList<BlockAuditEvent>> BlockLookupRadius()
    {
        return _runtime.BlockQueries.LookupAsync("main", _center, _lookupFilters);
    }

    [Benchmark]
    public Task<IReadOnlyList<BlockAuditEvent>> RollbackSelection()
    {
        return _runtime.Repository.SelectRollbackBlockEventsAsync(new RollbackBlockSelectionQuery(
            "main",
            _center,
            8,
            _lookupFilters.TimeRange,
            _lookupFilters.ActorName,
            _lookupFilters.Cause,
            _lookupFilters.Action,
            _lookupFilters.IncludeCodes,
            _lookupFilters.ExcludeCodes,
            128));
    }

    [Benchmark]
    public Task<IReadOnlyList<ContainerAuditTransaction>> ContainerLookupRadius()
    {
        return _runtime.ContainerQueries.LookupAsync("main", _center, _lookupFilters with
        {
            IncludeBlocks = false,
            IncludeContainers = true,
            Action = null
        });
    }
}
