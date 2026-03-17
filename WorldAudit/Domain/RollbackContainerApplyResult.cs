namespace WorldAudit.Domain;

public sealed record RollbackContainerApplyResult(
    RollbackContainerApplyStatus Status,
    bool ChangedWorld,
    byte[]? PreviousSnapshot,
    string? ErrorText = null);
