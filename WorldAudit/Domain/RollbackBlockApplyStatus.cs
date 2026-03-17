namespace WorldAudit.Domain;

public enum RollbackBlockApplyStatus
{
    Applied,
    Conflict,
    Failed
}
