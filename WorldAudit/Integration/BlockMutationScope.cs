using WorldAudit.Domain;

namespace WorldAudit.Integration;

public sealed record BlockMutationScope(
    string ActorName,
    string? ActorExternalId,
    AuditCause Cause,
    bool IsTrusted = true,
    byte[]? OldBlockEntitySnapshot = null,
    DateTimeOffset? ExpiresAt = null)
{
    public bool IsSynthetic => Cause != AuditCause.Player;

    public static BlockMutationScope Player(
        string actorName,
        string? actorExternalId,
        byte[]? oldBlockEntitySnapshot = null,
        DateTimeOffset? expiresAt = null)
    {
        return new BlockMutationScope(actorName, actorExternalId, AuditCause.Player, true, oldBlockEntitySnapshot, expiresAt);
    }

    public static BlockMutationScope Synthetic(
        AuditCause cause,
        DateTimeOffset? expiresAt = null)
    {
        return new BlockMutationScope($"#{cause.ToString().ToLowerInvariant()}", null, cause, true, null, expiresAt);
    }
}
