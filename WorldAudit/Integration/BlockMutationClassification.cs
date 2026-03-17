using WorldAudit.Domain;

namespace WorldAudit.Integration;

public sealed record BlockMutationClassification(
    string ActorName,
    string? ActorExternalId,
    AuditCause Cause,
    bool IsSynthetic)
{
    public static BlockMutationClassification Synthetic(AuditCause cause)
    {
        return new BlockMutationClassification($"#{cause.ToString().ToLowerInvariant()}", null, cause, true);
    }
}
