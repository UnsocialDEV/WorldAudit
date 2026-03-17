using WorldAudit.Application;
using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;

namespace WorldAudit.Tests;

public sealed class BlockMutationCaptureServiceTests
{
    [Fact]
    public async Task Observe_PlayerScopedPlacement_QueuesPlayerEvent()
    {
        var writer = new FakeAuditWriter();
        var guard = new WorldMutationGuard();
        var service = CreateService(writer, guard);

        await service.ObserveAsync(new BlockMutationObservation(
            WorldId: "main",
            Position: new BlockPosition(1, 65, 1),
            OccurredAt: DateTimeOffset.UtcNow,
            Action: BlockAuditAction.Place,
            OldBlockCode: "game:air",
            NewBlockCode: "game:stonebrick",
            PositionScope: BlockMutationScope.Player("Dayton", "player-1")));

        var auditEvent = Assert.Single(writer.BlockEvents);
        Assert.Equal(AuditCause.Player, auditEvent.Cause);
        Assert.Equal("Dayton", auditEvent.Actor.Name);
        Assert.False(auditEvent.Actor.IsSynthetic);
    }

    [Fact]
    public async Task Observe_ExplosionScope_QueuesSyntheticExplosionActor()
    {
        var writer = new FakeAuditWriter();
        var service = CreateService(writer);

        await service.ObserveAsync(new BlockMutationObservation(
            WorldId: "main",
            Position: new BlockPosition(2, 65, 2),
            OccurredAt: DateTimeOffset.UtcNow,
            Action: BlockAuditAction.Replace,
            OldBlockCode: "game:granite",
            NewBlockCode: "game:air",
            AmbientScope: BlockMutationScope.Synthetic(AuditCause.Explosion)));

        var auditEvent = Assert.Single(writer.BlockEvents);
        Assert.Equal(AuditCause.Explosion, auditEvent.Cause);
        Assert.Equal("#explosion", auditEvent.Actor.Name);
        Assert.True(auditEvent.Actor.IsSynthetic);
    }

    [Fact]
    public async Task Observe_FireContext_UsesFireCause()
    {
        var writer = new FakeAuditWriter();
        var service = CreateService(writer);

        await service.ObserveAsync(new BlockMutationObservation(
            WorldId: "main",
            Position: new BlockPosition(3, 65, 3),
            OccurredAt: DateTimeOffset.UtcNow,
            Action: BlockAuditAction.Break,
            OldBlockCode: "game:drygrass",
            NewBlockCode: "game:air",
            HasFireNeighbor: true));

        var auditEvent = Assert.Single(writer.BlockEvents);
        Assert.Equal(AuditCause.Fire, auditEvent.Cause);
        Assert.Equal("#fire", auditEvent.Actor.Name);
    }

    [Fact]
    public async Task Observe_GravityTokens_ClassifyBreakAndReplaceEvents()
    {
        var writer = new FakeAuditWriter();
        var service = CreateService(writer);

        await service.ObserveAsync(new BlockMutationObservation(
            WorldId: "main",
            Position: new BlockPosition(4, 70, 4),
            OccurredAt: DateTimeOffset.UtcNow,
            Action: BlockAuditAction.Break,
            OldBlockCode: "game:sand",
            NewBlockCode: "game:air"));

        await service.ObserveAsync(new BlockMutationObservation(
            WorldId: "main",
            Position: new BlockPosition(4, 69, 4),
            OccurredAt: DateTimeOffset.UtcNow,
            Action: BlockAuditAction.Place,
            OldBlockCode: "game:air",
            NewBlockCode: "game:sand"));

        Assert.Equal(2, writer.BlockEvents.Count);
        Assert.All(writer.BlockEvents, auditEvent =>
        {
            Assert.Equal(AuditCause.Gravity, auditEvent.Cause);
            Assert.Equal("#gravity", auditEvent.Actor.Name);
        });
    }

    [Fact]
    public async Task Observe_DecayTokens_UseDecayCause()
    {
        var writer = new FakeAuditWriter();
        var service = CreateService(writer);

        await service.ObserveAsync(new BlockMutationObservation(
            WorldId: "main",
            Position: new BlockPosition(5, 65, 5),
            OccurredAt: DateTimeOffset.UtcNow,
            Action: BlockAuditAction.Break,
            OldBlockCode: "game:rottedbeam",
            NewBlockCode: "game:air"));

        var auditEvent = Assert.Single(writer.BlockEvents);
        Assert.Equal(AuditCause.Decay, auditEvent.Cause);
        Assert.Equal("#decay", auditEvent.Actor.Name);
    }

    [Fact]
    public async Task Observe_SystemScope_UsesSystemCause()
    {
        var writer = new FakeAuditWriter();
        var service = CreateService(writer);

        await service.ObserveAsync(new BlockMutationObservation(
            WorldId: "main",
            Position: new BlockPosition(6, 65, 6),
            OccurredAt: DateTimeOffset.UtcNow,
            Action: BlockAuditAction.Replace,
            OldBlockCode: "game:stone",
            NewBlockCode: "game:air",
            AmbientScope: BlockMutationScope.Synthetic(AuditCause.System)));

        var auditEvent = Assert.Single(writer.BlockEvents);
        Assert.Equal(AuditCause.System, auditEvent.Cause);
        Assert.Equal("#system", auditEvent.Actor.Name);
    }

    [Fact]
    public async Task Observe_UnknownFallback_UsesUnknownActor()
    {
        var writer = new FakeAuditWriter();
        var service = CreateService(writer);

        await service.ObserveAsync(new BlockMutationObservation(
            WorldId: "main",
            Position: new BlockPosition(7, 65, 7),
            OccurredAt: DateTimeOffset.UtcNow,
            Action: BlockAuditAction.Replace,
            OldBlockCode: "game:stone",
            NewBlockCode: "game:air"));

        var auditEvent = Assert.Single(writer.BlockEvents);
        Assert.Equal(AuditCause.Unknown, auditEvent.Cause);
        Assert.Equal("#unknown", auditEvent.Actor.Name);
    }

    [Fact]
    public async Task Observe_DisabledProvider_FallsBackToUnknown()
    {
        var writer = new FakeAuditWriter();
        var service = CreateService(
            writer,
            options: new WorldAuditOptions
            {
                EnableNaturalCauseAudit = true,
                EnableFireCauseProvider = false
            });

        await service.ObserveAsync(new BlockMutationObservation(
            WorldId: "main",
            Position: new BlockPosition(8, 65, 8),
            OccurredAt: DateTimeOffset.UtcNow,
            Action: BlockAuditAction.Break,
            OldBlockCode: "game:drygrass",
            NewBlockCode: "game:air",
            HasFireNeighbor: true));

        var auditEvent = Assert.Single(writer.BlockEvents);
        Assert.Equal(AuditCause.Unknown, auditEvent.Cause);
        Assert.Equal("#unknown", auditEvent.Actor.Name);
    }

    [Fact]
    public async Task Observe_BlankClassifierActorName_FallsBackToCauseActor()
    {
        var writer = new FakeAuditWriter();
        var service = new BlockMutationCaptureService(
            writer,
            new WorldMutationGuard(),
            new StubClassifier(new BlockMutationClassification(" ", null, AuditCause.Fire, true)));

        await service.ObserveAsync(new BlockMutationObservation(
            WorldId: "main",
            Position: new BlockPosition(8, 66, 8),
            OccurredAt: DateTimeOffset.UtcNow,
            Action: BlockAuditAction.Break,
            OldBlockCode: "game:drygrass",
            NewBlockCode: "game:air",
            HasFireNeighbor: true));

        var auditEvent = Assert.Single(writer.BlockEvents);
        Assert.Equal(AuditCause.Fire, auditEvent.Cause);
        Assert.Equal("#fire", auditEvent.Actor.Name);
        Assert.True(auditEvent.Actor.IsSynthetic);
    }

    [Fact]
    public async Task Observe_SuppressesGuardedMutations()
    {
        var writer = new FakeAuditWriter();
        var guard = new WorldMutationGuard();
        var service = CreateService(writer, guard);
        var position = new BlockPosition(9, 65, 9);

        using (guard.Begin("main", position))
        {
            await service.ObserveAsync(new BlockMutationObservation(
                WorldId: "main",
                Position: position,
                OccurredAt: DateTimeOffset.UtcNow,
                Action: BlockAuditAction.Replace,
                OldBlockCode: "game:stone",
                NewBlockCode: "game:air",
                AmbientScope: BlockMutationScope.Synthetic(AuditCause.System)));
        }

        Assert.Empty(writer.BlockEvents);
    }

    private static BlockMutationCaptureService CreateService(
        FakeAuditWriter writer,
        WorldMutationGuard? guard = null,
        WorldAuditOptions? options = null)
    {
        guard ??= new WorldMutationGuard();
        options ??= new WorldAuditOptions();
        var classifier = new BlockMutationClassifierChain(options);
        return new BlockMutationCaptureService(writer, guard, classifier);
    }

    private sealed class FakeAuditWriter : IAuditWriter
    {
        public List<BlockAuditEvent> BlockEvents { get; } = [];

        public ValueTask QueueBlockEventAsync(BlockAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            BlockEvents.Add(auditEvent);
            return ValueTask.CompletedTask;
        }

        public ValueTask QueueContainerTransactionAsync(ContainerAuditTransaction transaction, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public AuditWriterSnapshot Snapshot()
        {
            return new AuditWriterSnapshot(0, 0, 0, TimeSpan.Zero, null);
        }
    }

    private sealed class StubClassifier : IBlockMutationClassifier
    {
        private readonly BlockMutationClassification _classification;

        public StubClassifier(BlockMutationClassification classification)
        {
            _classification = classification;
        }

        public BlockMutationClassification Classify(BlockMutationObservation observation)
        {
            return _classification;
        }
    }
}
