using WorldAudit.Application.Abstractions;
using WorldAudit.Application.Configuration;
using WorldAudit.Domain;
using WorldAudit.Integration;

namespace WorldAudit.Application.Services;

public sealed class BlockMutationClassifierChain(WorldAuditOptions options) : IBlockMutationClassifier
{
    public BlockMutationClassification Classify(BlockMutationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        // Operation context is evidence; names and neighboring materials are not.
        var scope = observation.AmbientScope ?? observation.PositionScope;
        if (scope is not { IsTrusted: true } || !Enabled(scope.Cause))
            return BlockMutationClassification.Synthetic(AuditCause.Unknown);
        return new BlockMutationClassification(scope.ActorName, scope.ActorExternalId, scope.Cause, scope.IsSynthetic);
    }

    private bool Enabled(AuditCause cause) => cause switch
    {
        AuditCause.Player => true,
        AuditCause.Explosion => options.EnableExplosionCauseProvider,
        AuditCause.System => options.EnableSystemCauseProvider,
        AuditCause.Worldgen => options.EnableWorldgenCauseProvider,
        AuditCause.Fire => options.EnableNaturalCauseAudit && options.EnableFireCauseProvider,
        AuditCause.Gravity => options.EnableNaturalCauseAudit && options.EnableGravityCauseProvider,
        AuditCause.Decay => options.EnableNaturalCauseAudit && options.EnableDecayCauseProvider,
        AuditCause.Liquid => options.EnableNaturalCauseAudit,
        _ => false
    };
}
