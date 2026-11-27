using WorldAudit.Domain;

namespace WorldAudit.Infrastructure.Queuing;

internal static class ContainerPayloadSize
{
    public static long Estimate(ContainerAuditTransaction row) => 512L
        + 2L * (row.WorldId.Length + (long)row.Actor.Name.Length + (row.Actor.ExternalId?.Length ?? 0)
            + row.InventoryType.Length + (row.ContainerLabel?.Length ?? 0) + (row.OperationId?.Length ?? 0) + (row.VisitId?.Length ?? 0))
        + (row.BeforeSnapshot?.LongLength ?? 0) + (row.AfterSnapshot?.LongLength ?? 0)
        + row.Lines.Sum(line => 192L + 2L * line.ItemCode.Length
            + (line.StackBeforeSnapshot?.LongLength ?? 0) + (line.StackAfterSnapshot?.LongLength ?? 0));
}
