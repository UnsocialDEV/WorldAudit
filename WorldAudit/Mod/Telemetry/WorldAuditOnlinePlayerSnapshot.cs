namespace WorldAudit.Mod.Telemetry;

internal sealed record WorldAuditOnlinePlayerSnapshot(int Count, IReadOnlyList<string> Names);
