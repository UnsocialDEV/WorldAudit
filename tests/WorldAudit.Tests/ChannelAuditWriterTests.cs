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

    [Fact]
    public async Task ConcurrentFlushRegistrationAndConsumerFailure_LeaveNoWaitersOrPendingRecords()
    {
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = WorldAudit.Tests.TestSupport.ProxyFactory.Create<IAuditRepository>(proxy =>
            proxy.SetHandler(nameof(IAuditRepository.WriteBlockEventsAsync), _ =>
            {
                writing.SetResult();
                return release.Task;
            }));
        var writer = new ChannelAuditWriter(repository, CreateOptions());
        writer.QueueBlockEvent(CreateEvent());
        var beforeFailure = writer.FlushAsync();
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiters = Enumerable.Range(0, 64).Select(async _ =>
        {
            await start.Task;
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.FlushAsync());
        }).ToArray();
        start.SetResult();
        release.SetException(new InvalidOperationException("Test writer failed."));
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => beforeFailure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.FlushAsync());
        Assert.Equal(0, writer.Snapshot().PendingEvents);
        Assert.Equal(1, writer.Snapshot().RejectedEvents);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BacklogBudgetIncludesBufferedAndInFlightEvents(bool pause)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = WorldAudit.Tests.TestSupport.ProxyFactory.Create<IAuditRepository>(proxy =>
            proxy.SetHandler(nameof(IAuditRepository.WriteBlockEventsAsync), _ =>
            {
                started.TrySetResult();
                return release.Task;
            }));
        await using var writer = new ChannelAuditWriter(repository, CreateOptions());
        var row = CreateEvent() with { OldBlockEntitySnapshot = new byte[1024 * 1024] };
        if (pause) writer.Pause();
        writer.QueueBlockEvent(row);
        if (!pause) await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            for (var i = 1; i < 63; i++) writer.QueueBlockEvent(row);
            Assert.Throws<InvalidOperationException>(() => writer.QueueBlockEvent(row));
            Assert.Equal(1, writer.Snapshot().RejectedEvents);
            Assert.Equal(63, writer.Snapshot().PendingEvents);
        }
        finally { release.TrySetResult(); }
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(63, writer.Snapshot().PersistedEvents);
        Assert.Equal(0, writer.Snapshot().PendingEvents);
        writer.QueueBlockEvent(row);
        await writer.FlushAsync();
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

        public Task<RollbackJob> FreezeRollbackPreviewAsync(WorldAudit.Application.Services.RollbackPreviewSelection selection, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RollbackPlanEntry>> GetRollbackPlanBatchAsync(long jobId, int limit, CancellationToken cancellationToken = default, bool validateSources = false)
            => throw new NotSupportedException();

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
