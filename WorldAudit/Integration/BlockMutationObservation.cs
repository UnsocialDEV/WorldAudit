using WorldAudit.Domain;

namespace WorldAudit.Integration;

public sealed class BlockMutationObservation
{
    public BlockMutationObservation(
        string WorldId,
        BlockPosition Position,
        DateTimeOffset OccurredAt,
        BlockAuditAction Action,
        string? OldBlockCode,
        string? NewBlockCode,
        byte[]? OldBlockEntitySnapshot = null,
        byte[]? NewBlockEntitySnapshot = null,
        int Flags = 0,
        BlockMutationScope? PositionScope = null,
        BlockMutationScope? AmbientScope = null,
        string? CurrentBlockCode = null,
        bool HasFireNeighbor = false)
    {
        this.WorldId = WorldId;
        this.Position = Position;
        this.OccurredAt = OccurredAt;
        this.Action = Action;
        this.OldBlockCode = OldBlockCode;
        this.NewBlockCode = NewBlockCode;
        this.OldBlockEntitySnapshot = OldBlockEntitySnapshot;
        this.NewBlockEntitySnapshot = NewBlockEntitySnapshot;
        this.Flags = Flags;
        this.PositionScope = PositionScope;
        this.AmbientScope = AmbientScope;
        this.CurrentBlockCode = CurrentBlockCode;
        this.HasFireNeighbor = HasFireNeighbor;
    }

    public string WorldId { get; }

    public BlockPosition Position { get; }

    public DateTimeOffset OccurredAt { get; }

    public BlockAuditAction Action { get; }

    public string? OldBlockCode { get; }

    public string? NewBlockCode { get; }

    public byte[]? OldBlockEntitySnapshot { get; }

    public byte[]? NewBlockEntitySnapshot { get; }

    public int Flags { get; }

    public BlockMutationScope? PositionScope { get; }

    public BlockMutationScope? AmbientScope { get; }

    public string? CurrentBlockCode { get; }

    public bool HasFireNeighbor { get; }
}
