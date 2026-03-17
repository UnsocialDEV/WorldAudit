namespace WorldAudit.Application;

public sealed record PurgePreview(
    DateTimeOffset Cutoff,
    int BlockEventCount,
    int ContainerTransactionCount,
    int RollbackJobCount)
{
    public int TotalCount => BlockEventCount + ContainerTransactionCount + RollbackJobCount;
}
