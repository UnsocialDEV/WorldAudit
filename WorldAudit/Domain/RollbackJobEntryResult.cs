namespace WorldAudit.Domain;

public enum RollbackJobEntryResult
{
    Planned,
    Applied,
    Conflict,
    Failed
}
