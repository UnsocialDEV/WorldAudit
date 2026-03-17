namespace WorldAudit.Domain;

public enum RollbackJobState
{
    Planned,
    Previewed,
    Queued,
    Running,
    Completed,
    CompletedWithConflicts,
    Failed
}
