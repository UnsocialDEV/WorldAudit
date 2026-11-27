using System.Collections.Concurrent;
using System.Threading.Channels;
using WorldAudit.Application;
using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Application.Services;
using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Queuing;

public sealed class ChannelAuditWriter : IAuditWriter, IAsyncDisposable
{
    private readonly Channel<QueueItem> _channel;
    private readonly IAuditRepository _repository;
    private readonly WorldAuditOptions _options;
    private readonly AuditPerformanceDiagnostics? _diagnostics;
    private readonly Task _consumerTask;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _flushWaiters = [];
    private const int MaxPendingEvents = 8192;
    private const long MaxPendingPayloadBytes = 64L * 1024 * 1024;
    private readonly object _admission = new();
    private long _pendingBytes;
    private long _rejectedEvents;
    private Task? _currentFlush;
    private long _pendingEvents;
    private long _persistedEvents;
    private long _nextFlushId;
    private int _flushCount;
    private long _lastFlushDurationTicks;
    private int _lastFlushBatchSize;
    private int _largestFlushBatchSize;
    private int _slowFlushCount;
    private long _slowestFlushDurationTicks;
    private Exception? _consumerException;
    private int _isPaused;
    private int _resumeSignalQueued;

    public ChannelAuditWriter(IAuditRepository repository, WorldAuditOptions options, AuditPerformanceDiagnostics? diagnostics = null)
    {
        _repository = repository;
        _options = options;
        _diagnostics = diagnostics;
        _channel = Channel.CreateUnbounded<QueueItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _consumerTask = Task.Run(ProcessQueueAsync);
    }

    public void QueueBlockEvent(BlockAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (FireBlock.Matches(auditEvent.OldBlockCode) && FireBlock.Matches(auditEvent.NewBlockCode)) return;
        EnqueueEvent(new EventItem(auditEvent), BlockPayloadSize.Estimate(auditEvent));
    }

    public ValueTask QueueBlockEventAsync(BlockAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueueBlockEvent(auditEvent);
        return ValueTask.CompletedTask;
    }

    public void QueueContainerTransaction(ContainerAuditTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        EnqueueEvent(new ContainerTransactionItem(transaction), ContainerPayloadSize.Estimate(transaction));
    }

    public ValueTask QueueContainerTransactionAsync(ContainerAuditTransaction transaction, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueueContainerTransaction(transaction);
        return ValueTask.CompletedTask;
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_admission)
        {
            ThrowIfFaulted();
            if (_pendingEvents == 0) return Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (_currentFlush is null)
            {
                var id = ++_nextFlushId;
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _flushWaiters[id] = completion;
                if (!_channel.Writer.TryWrite(new FlushItem(id)))
                {
                    _flushWaiters.TryRemove(id, out _);
                    throw new InvalidOperationException("The audit writer is closed.");
                }
                _currentFlush = completion.Task;
            }
            return _currentFlush.WaitAsync(cancellationToken);
        }
    }

    public void Pause()
    {
        ThrowIfFaulted();
        Interlocked.Exchange(ref _isPaused, 1);
    }

    public void Resume()
    {
        ThrowIfFaulted();
        if (Interlocked.Exchange(ref _isPaused, 0) == 1 && Interlocked.Exchange(ref _resumeSignalQueued, 1) == 0)
        {
            _channel.Writer.TryWrite(ResumeSignalItem.Instance);
        }
    }

    public bool IsPaused => Volatile.Read(ref _isPaused) == 1;

    public AuditWriterSnapshot Snapshot()
    {
        return new AuditWriterSnapshot(
            PendingEvents: Volatile.Read(ref _pendingEvents),
            PersistedEvents: Volatile.Read(ref _persistedEvents),
            FlushCount: Volatile.Read(ref _flushCount),
            LastFlushDuration: TimeSpan.FromTicks(Volatile.Read(ref _lastFlushDurationTicks)),
            LastError: _consumerException?.Message,
            IsPaused: IsPaused,
            LastFlushBatchSize: Volatile.Read(ref _lastFlushBatchSize),
            LargestFlushBatchSize: Volatile.Read(ref _largestFlushBatchSize),
            SlowFlushCount: Volatile.Read(ref _slowFlushCount),
            SlowestFlushDuration: TimeSpan.FromTicks(Volatile.Read(ref _slowestFlushDurationTicks)),
            RejectedEvents: Volatile.Read(ref _rejectedEvents));
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _consumerTask.ConfigureAwait(false);
        ThrowIfFaulted();
    }

    private async Task ProcessQueueAsync()
    {
        var blockBatch = new List<BlockAuditEvent>(Math.Min(MaxPendingEvents, _options.WriterBatchSize));
        var containerBatch = new List<ContainerAuditTransaction>(Math.Min(MaxPendingEvents, _options.WriterBatchSize));
        Exception? consumerException = null;

        try
        {
            while (true)
            {
                QueueItem item;
                try
                {
                    item = await _channel.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                if (!await HandleItemAsync(item, blockBatch, containerBatch, CancellationToken.None).ConfigureAwait(false))
                {
                    continue;
                }

                if (IsPaused)
                {
                    continue;
                }

                using var batchWindow = new CancellationTokenSource(_options.WriterMaxFlushDelay);


                while (blockBatch.Count + containerBatch.Count < _options.WriterBatchSize)
                {
                    if (_channel.Reader.TryRead(out var bufferedItem))
                    {
                        item = bufferedItem;
                        if (!await HandleItemAsync(item, blockBatch, containerBatch, CancellationToken.None).ConfigureAwait(false))
                        {
                            continue;
                        }

                        if (IsPaused)
                        {
                            break;
                        }

                        if (blockBatch.Count + containerBatch.Count >= _options.WriterBatchSize)
                        {
                            break;
                        }

                        continue;
                    }

                    try
                    {
                        item = await _channel.Reader.ReadAsync(batchWindow.Token).ConfigureAwait(false);
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }
                    catch (OperationCanceledException) when (batchWindow.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!await HandleItemAsync(item, blockBatch, containerBatch, CancellationToken.None).ConfigureAwait(false))
                    {
                        continue;
                    }

                    if (IsPaused)
                    {
                        break;
                    }
                }

                if (!IsPaused && (blockBatch.Count > 0 || containerBatch.Count > 0))
                {
                    await PersistBatchAsync(blockBatch, containerBatch, CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (blockBatch.Count > 0 || containerBatch.Count > 0)
            {
                await PersistBatchAsync(blockBatch, containerBatch, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            consumerException = exception;
            lock (_admission)
            {
                Interlocked.CompareExchange(ref _consumerException, exception, null);
                _channel.Writer.TryComplete(exception);
                Interlocked.Add(ref _rejectedEvents, _pendingEvents);
                _pendingEvents = 0;
                _pendingBytes = 0;
            }
            while (_channel.Reader.TryRead(out _)) { }
        }
        finally
        {
            FailOutstandingFlushWaiters(consumerException ?? CreateIncompleteFlushException());
        }
    }

    private async Task<bool> HandleItemAsync(
        QueueItem item,
        List<BlockAuditEvent> blockBatch,
        List<ContainerAuditTransaction> containerBatch,
        CancellationToken cancellationToken)
    {
        switch (item)
        {
            case EventItem eventItem:
                blockBatch.Add(eventItem.Event);
                return true;

            case ContainerTransactionItem transactionItem:
                containerBatch.Add(transactionItem.Transaction);
                return true;

            case FlushItem flushItem:
                if (blockBatch.Count > 0 || containerBatch.Count > 0)
                {
                    await PersistBatchAsync(blockBatch, containerBatch, cancellationToken).ConfigureAwait(false);
                }

                CompleteFlushWaiter(flushItem.FlushId);
                return false;

            case ResumeSignalItem:
                Volatile.Write(ref _resumeSignalQueued, 0);
                if (!IsPaused && (blockBatch.Count > 0 || containerBatch.Count > 0))
                {
                    await PersistBatchAsync(blockBatch, containerBatch, cancellationToken).ConfigureAwait(false);
                }

                return false;

            default:
                return false;
        }
    }

    private async Task PersistBatchAsync(
        List<BlockAuditEvent> blockBatch,
        List<ContainerAuditTransaction> containerBatch,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var persistedCount = blockBatch.Count + containerBatch.Count;
        if (blockBatch.Count > 0)
        {
            await _repository.WriteBlockEventsAsync(blockBatch, cancellationToken).ConfigureAwait(false);
            ReleasePersisted(blockBatch.Count, blockBatch.Sum(BlockPayloadSize.Estimate));
        }

        if (containerBatch.Count > 0)
        {
            await _repository.WriteContainerTransactionsAsync(containerBatch, cancellationToken).ConfigureAwait(false);
            ReleasePersisted(containerBatch.Count, containerBatch.Sum(ContainerPayloadSize.Estimate));
        }

        var duration = DateTime.UtcNow - started;
        Interlocked.Increment(ref _flushCount);
        Interlocked.Exchange(ref _lastFlushDurationTicks, duration.Ticks);
        Interlocked.Exchange(ref _lastFlushBatchSize, persistedCount);
        UpdateLargestBatchSize(persistedCount);
        UpdateSlowFlushMetrics(persistedCount, duration);
        blockBatch.Clear();
        containerBatch.Clear();
    }

    private void UpdateLargestBatchSize(int persistedCount)
    {
        while (true)
        {
            var current = Volatile.Read(ref _largestFlushBatchSize);
            if (persistedCount <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _largestFlushBatchSize, persistedCount, current) == current)
            {
                return;
            }
        }
    }

    private void UpdateSlowFlushMetrics(int persistedCount, TimeSpan duration)
    {
        if (duration.TotalMilliseconds >= _options.SlowFlushThresholdMilliseconds)
        {
            Interlocked.Increment(ref _slowFlushCount);
            _diagnostics?.LogVerbose($"Slow flush persisted {persistedCount} event(s) in {duration.TotalMilliseconds:0.0} ms.");
        }

        while (true)
        {
            var current = Volatile.Read(ref _slowestFlushDurationTicks);
            if (duration.Ticks <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _slowestFlushDurationTicks, duration.Ticks, current) == current)
            {
                return;
            }
        }
    }

    private void ThrowIfFaulted()
    {
        if (_consumerException is not null)
        {
            throw new InvalidOperationException("The audit writer is faulted.", _consumerException);
        }
    }

    private void EnqueueEvent(QueueItem item, long payloadBytes)
    {
        lock (_admission)
        {
            if (_consumerException is not null || _pendingEvents >= MaxPendingEvents ||
                payloadBytes > MaxPendingPayloadBytes - _pendingBytes || !_channel.Writer.TryWrite(item))
            {
                Interlocked.Increment(ref _rejectedEvents);
                ThrowIfFaulted();
                throw new InvalidOperationException("Audit record rejected because the writer is full or closed. See /wa status.");
            }

            _pendingEvents++;
            _pendingBytes += payloadBytes;
            _currentFlush = null;
        }
    }

    private void ReleasePersisted(int count, long bytes)
    {
        lock (_admission)
        {
            _pendingEvents -= count;
            _pendingBytes -= bytes;
            Interlocked.Add(ref _persistedEvents, count);
        }
    }

    private void CompleteFlushWaiter(long flushId)
    {
        if (_flushWaiters.TryRemove(flushId, out var completion))
        {
            completion.TrySetResult(true);
        }
    }

    private void FailOutstandingFlushWaiters(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        foreach (var flushWaiter in _flushWaiters)
        {
            if (_flushWaiters.TryRemove(flushWaiter.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private Exception? CreateIncompleteFlushException()
    {
        return _flushWaiters.IsEmpty
            ? null
            : new InvalidOperationException("The audit writer stopped before all flush requests completed.");
    }

    private abstract record QueueItem;

    private sealed record EventItem(BlockAuditEvent Event) : QueueItem;

    private sealed record ContainerTransactionItem(ContainerAuditTransaction Transaction) : QueueItem;

    private sealed record FlushItem(long FlushId) : QueueItem;

    private sealed record ResumeSignalItem : QueueItem
    {
        public static ResumeSignalItem Instance { get; } = new();
    }
}
