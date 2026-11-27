using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Queuing;

internal static class BlockPayloadSize
{
    public static long Estimate(BlockAuditEvent row) => 512L
        + 2L * (row.WorldId.Length + (long)row.Actor.Name.Length + (row.Actor.ExternalId?.Length ?? 0)
            + (row.OldBlockCode?.Length ?? 0) + (row.NewBlockCode?.Length ?? 0) + (row.OperationId?.Length ?? 0))
        + (row.OldBlockEntitySnapshot?.LongLength ?? 0) + (row.NewBlockEntitySnapshot?.LongLength ?? 0);
}
