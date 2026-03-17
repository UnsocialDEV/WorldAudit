using WorldAudit.Domain;

namespace WorldAudit.Application.Abstractions;

public interface IAuditWriter
{
    void QueueBlockEvent(BlockAuditEvent auditEvent)
    {
        QueueBlockEventAsync(auditEvent).GetAwaiter().GetResult();
    }

    ValueTask QueueBlockEventAsync(BlockAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueueBlockEvent(auditEvent);
        return ValueTask.CompletedTask;
    }

    void QueueContainerTransaction(ContainerAuditTransaction transaction)
    {
        QueueContainerTransactionAsync(transaction).GetAwaiter().GetResult();
    }

    ValueTask QueueContainerTransactionAsync(ContainerAuditTransaction transaction, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueueContainerTransaction(transaction);
        return ValueTask.CompletedTask;
    }

    Task FlushAsync(CancellationToken cancellationToken = default);

    AuditWriterSnapshot Snapshot();
}
