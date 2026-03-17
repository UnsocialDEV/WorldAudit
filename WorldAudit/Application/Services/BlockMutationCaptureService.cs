using WorldAudit.Application.Abstractions;
using WorldAudit.Domain;
using WorldAudit.Integration;

namespace WorldAudit.Application.Services;

public sealed class BlockMutationCaptureService : IBlockMutationSink
{
    private readonly IAuditWriter _auditWriter;
    private readonly IWorldMutationGuard _mutationGuard;
    private readonly IBlockMutationClassifier _classifier;

    public BlockMutationCaptureService(
        IAuditWriter auditWriter,
        IWorldMutationGuard mutationGuard,
        IBlockMutationClassifier classifier)
    {
        _auditWriter = auditWriter;
        _mutationGuard = mutationGuard;
        _classifier = classifier;
    }

    public void Observe(BlockMutationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (_mutationGuard.IsActive(observation.WorldId, observation.Position))
        {
            return;
        }

        var classification = _classifier.Classify(observation);
        var actorName = AuditActor.NormalizeName(classification.ActorName, classification.Cause);
        var auditEvent = new BlockAuditEvent(
            Id: null,
            WorldId: observation.WorldId,
            Position: observation.Position,
            OccurredAt: observation.OccurredAt,
            Actor: new AuditActor(actorName, classification.ActorExternalId, classification.IsSynthetic),
            Cause: classification.Cause,
            Action: observation.Action,
            OldBlockCode: observation.OldBlockCode,
            NewBlockCode: observation.NewBlockCode,
            OldBlockEntitySnapshot: observation.OldBlockEntitySnapshot,
            NewBlockEntitySnapshot: observation.NewBlockEntitySnapshot,
            Flags: observation.Flags);

        _auditWriter.QueueBlockEvent(auditEvent);
    }

    public ValueTask ObserveAsync(BlockMutationObservation observation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Observe(observation);
        return ValueTask.CompletedTask;
    }
}
