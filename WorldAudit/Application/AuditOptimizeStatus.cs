namespace WorldAudit.Application;

public sealed record AuditOptimizeStatus(
    DateTimeOffset CompletedAt,
    TimeSpan Duration,
    string Reason);
