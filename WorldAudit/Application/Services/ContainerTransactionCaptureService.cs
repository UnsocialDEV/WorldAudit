using WorldAudit.Application.Abstractions;
using WorldAudit.Domain;
using WorldAudit.Integration;

namespace WorldAudit.Application.Services;

public sealed class ContainerTransactionCaptureService : IContainerTransactionCapture
{
    private readonly IAuditWriter _auditWriter;
    private readonly IWorldMutationGuard? _mutationGuard;

    public ContainerTransactionCaptureService(IAuditWriter auditWriter, IWorldMutationGuard? mutationGuard = null)
    {
        _auditWriter = auditWriter;
        _mutationGuard = mutationGuard;
    }

    public void Capture(ContainerTransactionCaptureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_mutationGuard?.IsActive(context.WorldId, context.Position) == true) return;

        var lines = new ContainerAuditLine[context.Lines.Count];
        for (var index = 0; index < context.Lines.Count; index++)
        {
            var line = context.Lines[index];
            lines[index] = new ContainerAuditLine(
                Id: null,
                ItemCode: line.ItemCode,
                SlotId: line.SlotId,
                QuantityDelta: line.QuantityDelta,
                BeforeQuantity: line.BeforeQuantity,
                AfterQuantity: line.AfterQuantity,
                StackBeforeSnapshot: line.StackBeforeSnapshot,
                StackAfterSnapshot: line.StackAfterSnapshot);
        }

        var transaction = new ContainerAuditTransaction(
            Id: null,
            WorldId: context.WorldId,
            Position: context.Position,
            OccurredAt: context.OccurredAt,
            Actor: new AuditActor(context.ActorName, context.ActorExternalId, context.Cause != AuditCause.Player && string.IsNullOrWhiteSpace(context.ActorExternalId)),
            Cause: context.Cause,
            InventoryType: context.InventoryType,
            ContainerLabel: context.ContainerLabel,
            BeforeSnapshot: context.BeforeSnapshot,
            AfterSnapshot: context.AfterSnapshot,
            Lines: lines,
            Flags: context.Flags,
            SourceJobId: context.SourceJobId);

        _auditWriter.QueueContainerTransaction(transaction);
    }

    public ValueTask CaptureAsync(ContainerTransactionCaptureContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Capture(context);
        return ValueTask.CompletedTask;
    }
}
