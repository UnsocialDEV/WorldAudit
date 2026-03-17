namespace WorldAudit.Domain;

public sealed record RollbackBlockApplyResult(
    RollbackBlockApplyStatus Status,
    bool ChangedWorld,
    string? PreviousBlockCode,
    string? ErrorText = null);
