using BenchmarkDotNet.Attributes;
using WorldAudit.Application;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Mod;

namespace WorldAudit.Benchmarks;

[MemoryDiagnoser]
public sealed class WriterAndMutationBenchmarks
{
    private BlockMutationClassifierChain _classifier = null!;
    private BlockMutationObservation _observation = null!;
    private string _databasePath = null!;
    private WorldAuditRuntime _runtime = null!;
    private int _flushCounter;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _classifier = new BlockMutationClassifierChain(new WorldAuditOptions());
        _observation = new BlockMutationObservation(
            WorldId: "main",
            Position: new BlockPosition(32, 70, 32),
            OccurredAt: DateTimeOffset.UtcNow,
            Action: BlockAuditAction.Replace,
            OldBlockCode: "game:drygrass",
            NewBlockCode: "game:air",
            CurrentBlockCode: "game:air",
            HasFireNeighbor: true);

        _databasePath = Path.Combine(Path.GetTempPath(), $"worldaudit-bench-writer-{Guid.NewGuid():N}.db");
        _runtime = await WorldAuditRuntime.CreateAsync(new WorldAuditOptions
        {
            DatabasePath = _databasePath,
            WriterBatchSize = 256,
            WriterMaxFlushDelay = TimeSpan.FromMilliseconds(10)
        });
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
    public BlockMutationClassification MutationClassification()
    {
        return _classifier.Classify(_observation);
    }

    [Benchmark]
    public async Task WriterFlushThroughput()
    {
        for (var index = 0; index < 128; index++)
        {
            await _runtime.BlockCapture.CaptureAsync(BlockChangeCaptureContext.PlayerPlaced(
                "main",
                new BlockPosition(_flushCounter, 65, index),
                DateTimeOffset.UtcNow,
                "Bench",
                "bench-1",
                "game:stone"));
        }

        _flushCounter++;
        await _runtime.FlushAsync();
    }
}
