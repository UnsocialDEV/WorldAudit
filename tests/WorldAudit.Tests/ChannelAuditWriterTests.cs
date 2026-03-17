using WorldAudit.Application;
using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Domain;
using WorldAudit.Infrastructure.Queuing;

namespace WorldAudit.Tests;

public sealed class ChannelAuditWriterTests
{
    [Fact]
    public async Task FlushAsync_ThrowsInsteadOfHangingWhenRepositoryFaults()
    {
        var writer = new ChannelAuditWriter(
            new FaultingAuditRepository(TimeSpan.Zero),
            CreateOptions());

        writer.QueueBlockEvent(CreateEvent());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("Write failed.", exception.Message);

        var faultedException = Assert.Throws<InvalidOperationException>(() => writer.QueueBlockEvent(CreateEvent()));
        Assert.Contains("faulted", faultedException.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task FlushAsync_FailsAllPendingWaitersWhenConsumerFaults()
    {
        var writer = new ChannelAuditWriter(
            new FaultingAuditRepository(TimeSpan.FromMilliseconds(100)),
            CreateOptions());

        writer.QueueBlockEvent(CreateEvent());

        var firstFlush = writer.FlushAsync();
        var secondFlush = writer.FlushAsync();

        var firstException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstFlush.WaitAsync(TimeSpan.FromSeconds(2)));
        var secondException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => secondFlush.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.Equal("Write failed.", firstException.Message);
        Assert.Equal("Write failed.", secondException.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static WorldAuditOptions CreateOptions()
    {
        return new WorldAuditOptions
        {
            WriterBatchSize = 8,
            WriterMaxFlushDelay = TimeSpan.FromMilliseconds(5)
        };
    }

    private static BlockAuditEvent CreateEvent()
    {
        return new BlockAuditEvent(
            Id: null,
            WorldId: "main",
            Position: new BlockPosition(1, 64, 1),
            OccurredAt: DateTimeOffset.UtcNow,
            Actor: new AuditActor("Dayton", "player-1"),
            Cause: AuditCause.Player,
            Action: BlockAuditAction.Place,
            OldBlockCode: "game:air",
            NewBlockCode: "game:stonebrick");
    }

    private sealed class FaultingAuditRepository : IAuditRepository
    {
        private readonly TimeSpan _delayBeforeThrow;

        public FaultingAuditRepository(TimeSpan delayBeforeThrow)
        {
            _delayBeforeThrow = delayBeforeThrow;
        }

        public async Task WriteBlockEventsAsync(IReadOnlyList<BlockAuditEvent> events, CancellationToken cancellationToken = default)
        {
            if (_delayBeforeThrow > TimeSpan.Zero)
            {
                await Task.Delay(_delayBeforeThrow, cancellationToken);
            }

            throw new InvalidOperationException("Write failed.");
        }

        public Task WriteContainerTransactionsAsync(IReadOnlyList<ContainerAuditTransaction> transactions, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<BlockAuditEvent>> GetBlockHistoryAsync(BlockHistoryQuery query, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<BlockAuditEvent>> LookupBlockEventsAsync(BlockLookupQuery query, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<BlockAuditEvent>> SelectRollbackBlockEventsAsync(RollbackBlockSelectionQuery query, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ContainerAuditTransaction>> GetContainerHistoryAsync(ContainerHistoryQuery query, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ContainerAuditTransaction>> LookupContainerTransactionsAsync(ContainerLookupQuery query, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ContainerAuditTransaction>> SelectRollbackContainerTransactionsAsync(ContainerRollbackSelectionQuery query, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RollbackJob> CreateRollbackJobAsync(RollbackJobCreateRequest request, IReadOnlyList<RollbackJobPlanEntryCreate> entries, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RollbackJob?> GetRollbackJobAsync(long jobId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RollbackJob>> GetRollbackJobsAsync(int limit = 10, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<BlockAuditEvent>> GetRollbackJobSourceBlockEventsAsync(long jobId, bool appliedOnly, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ContainerAuditTransaction>> GetRollbackJobSourceContainerTransactionsAsync(long jobId, bool appliedOnly, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RollbackPlanEntry>> GetRollbackPlanEntriesAsync(long jobId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RollbackBlockPlanEntry>> GetRollbackBlockPlanEntriesAsync(long jobId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RollbackJobEntryDetail>> GetRollbackJobEntryDetailsAsync(long jobId, RollbackJobEntryResult result, int limit = 10, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task UpdateRollbackJobStateAsync(long jobId, RollbackJobState state, DateTimeOffset? queuedAt = null, DateTimeOffset? startedAt = null, DateTimeOffset? completedAt = null, string? lastError = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task RecordRollbackBatchAsync(long jobId, IReadOnlyList<RollbackBatchEntryUpdate> updates, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
