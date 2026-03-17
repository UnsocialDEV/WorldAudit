using WorldAudit.Domain;

namespace WorldAudit.Integration;

public sealed record BlockChangeCaptureContext(
    string WorldId,
    BlockPosition Position,
    DateTimeOffset OccurredAt,
    string ActorName,
    string? ActorExternalId,
    AuditCause Cause,
    BlockAuditAction Action,
    string? OldBlockCode,
    string? NewBlockCode,
    byte[]? OldBlockEntitySnapshot = null,
    byte[]? NewBlockEntitySnapshot = null,
    int Flags = 0)
{
    public static BlockChangeCaptureContext PlayerPlaced(
        string worldId,
        BlockPosition position,
        DateTimeOffset occurredAt,
        string actorName,
        string? actorExternalId,
        string newBlockCode)
    {
        return new BlockChangeCaptureContext(
            worldId,
            position,
            occurredAt,
            actorName,
            actorExternalId,
            AuditCause.Player,
            BlockAuditAction.Place,
            OldBlockCode: "game:air",
            NewBlockCode: newBlockCode);
    }

    public static BlockChangeCaptureContext PlayerBroke(
        string worldId,
        BlockPosition position,
        DateTimeOffset occurredAt,
        string actorName,
        string? actorExternalId,
        string oldBlockCode)
    {
        return new BlockChangeCaptureContext(
            worldId,
            position,
            occurredAt,
            actorName,
            actorExternalId,
            AuditCause.Player,
            BlockAuditAction.Break,
            OldBlockCode: oldBlockCode,
            NewBlockCode: "game:air");
    }
}
