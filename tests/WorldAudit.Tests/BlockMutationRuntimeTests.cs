using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;
using WorldAudit.Mod;

namespace WorldAudit.Tests;

public sealed class BlockMutationRuntimeTests : IAsyncLifetime
{
    private string _databasePath = null!;
    private WorldAuditRuntime _runtime = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"worldaudit-mutations-{Guid.NewGuid():N}.db");
        _runtime = await WorldAuditRuntime.CreateAsync(
            new WorldAuditOptions
            {
                DatabasePath = _databasePath,
                WriterBatchSize = 2,
                WriterMaxFlushDelay = TimeSpan.FromMilliseconds(25),
                DefaultPageSize = 8
            }).WaitAsync(TimeSpan.FromSeconds(5));
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MutationCapture_PersistsSyntheticActor_AndFormatterShowsIt()
    {
        var position = new BlockPosition(12, 64, 12);
        var parser = new LookupCommandParser();
        var explosionScope = BlockMutationScope.Synthetic(AuditCause.Explosion);

        using (_runtime.BlockMutationScopes.BeginAmbientScope(explosionScope))
        {
            await _runtime.BlockMutationCapture.ObserveAsync(new BlockMutationObservation(
                WorldId: "main",
                Position: position,
                OccurredAt: new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero),
                Action: BlockAuditAction.Break,
                OldBlockCode: "game:stonebrick",
                NewBlockCode: "game:air",
                AmbientScope: explosionScope)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        await _runtime.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var history = await _runtime.BlockQueries.GetHistoryAsync("main", position).WaitAsync(TimeSpan.FromSeconds(5));
        var filters = parser.Parse(["u:#explosion"], DateTimeOffset.UtcNow);
        var lines = _runtime.ResultFormatter.FormatBlockResults(history, new DateTimeOffset(2026, 3, 16, 12, 5, 0, TimeSpan.Zero));

        Assert.Single(history);
        Assert.Equal("#explosion", history[0].Actor.Name);
        Assert.Equal(AuditCause.Explosion, history[0].Cause);
        Assert.Equal(AuditCause.Explosion, filters.Cause);
        Assert.Contains(lines, line => line.Contains("#explosion", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, line => line.Contains("cause: explosion", StringComparison.OrdinalIgnoreCase));
    }
}
