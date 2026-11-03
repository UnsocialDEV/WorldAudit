namespace WorldAudit.Domain;

public sealed record RollbackBlockApplyResult(
    RollbackBlockApplyStatus Status,
    bool ChangedWorld,
    string? PreviousBlockCode,
    string? ErrorText = null,
    byte[]? PreviousBlockEntitySnapshot = null,
    string? ActualBlockCode = null,
    byte[]? ActualBlockEntitySnapshot = null);
