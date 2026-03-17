namespace WorldAudit.Application;

public sealed record MaintenanceRunStatus(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Succeeded,
    int RetentionDays,
    DateTimeOffset? Cutoff,
    string Summary,
    string? Error = null);
