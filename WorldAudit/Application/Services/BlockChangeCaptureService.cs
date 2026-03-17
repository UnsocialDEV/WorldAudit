using WorldAudit.Application.Abstractions;
using WorldAudit.Domain;
using WorldAudit.Integration;

namespace WorldAudit.Application.Services;

public sealed class BlockChangeCaptureService : IBlockChangeCapture
{
    private readonly IAuditWriter _auditWriter;
    private readonly IWorldMutationGuard _mutationGuard;

    public BlockChangeCaptureService(IAuditWriter auditWriter, IWorldMutationGuard mutationGuard)
    {
        _auditWriter = auditWriter;
        _mutationGuard = mutationGuard;
    }

    public void Capture(BlockChangeCaptureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_mutationGuard.IsActive(context.WorldId, context.Position))
        {
            return;
        }

        var auditEvent = new BlockAuditEvent(
            Id: null,
            WorldId: context.WorldId,
            Position: context.Position,
            OccurredAt: context.OccurredAt,
            Actor: new AuditActor(context.ActorName, context.ActorExternalId, context.Cause != AuditCause.Player),
            Cause: context.Cause,
            Action: context.Action,
            OldBlockCode: context.OldBlockCode,
            NewBlockCode: context.NewBlockCode,
            OldBlockEntitySnapshot: context.OldBlockEntitySnapshot,
            NewBlockEntitySnapshot: context.NewBlockEntitySnapshot,
            Flags: context.Flags);

        _auditWriter.QueueBlockEvent(auditEvent);
    }

    public ValueTask CaptureAsync(BlockChangeCaptureContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Capture(context);
        return ValueTask.CompletedTask;
    }
}
