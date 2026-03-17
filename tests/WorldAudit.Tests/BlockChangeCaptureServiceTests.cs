using WorldAudit.Application.Abstractions;
using WorldAudit.Application;
using WorldAudit.Application.Services;
using WorldAudit.Domain;
using WorldAudit.Integration;

namespace WorldAudit.Tests;

public sealed class BlockChangeCaptureServiceTests
{
    [Fact]
    public async Task Capture_SuppressesGuardedMutations()
    {
        var writer = new FakeAuditWriter();
        var guard = new WorldMutationGuard();
        var service = new BlockChangeCaptureService(writer, guard);
        var position = new BlockPosition(7, 64, 7);

        using (guard.Begin("main", position))
        {
            await service.CaptureAsync(new BlockChangeCaptureContext(
                WorldId: "main",
                Position: position,
                OccurredAt: DateTimeOffset.UtcNow,
                ActorName: "Dayton",
                ActorExternalId: "player-1",
                Cause: AuditCause.Player,
                Action: BlockAuditAction.Break,
                OldBlockCode: "game:chest",
                NewBlockCode: "game:air"));
        }

        Assert.Empty(writer.BlockEvents);
    }

    [Fact]
    public async Task Capture_QueuesEventWhenMutationIsNotGuarded()
    {
        var writer = new FakeAuditWriter();
        var guard = new WorldMutationGuard();
        var service = new BlockChangeCaptureService(writer, guard);
        var position = new BlockPosition(8, 64, 8);

        await service.CaptureAsync(new BlockChangeCaptureContext(
            WorldId: "main",
            Position: position,
            OccurredAt: DateTimeOffset.UtcNow,
            ActorName: "Dayton",
            ActorExternalId: "player-1",
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Place,
            OldBlockCode: "game:air",
            NewBlockCode: "game:stonebrick"));

        Assert.Single(writer.BlockEvents);
        Assert.Equal(position, writer.BlockEvents[0].Position);
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
}
