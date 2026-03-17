namespace WorldAudit.Mod.Telemetry;

internal sealed record DiscordWebhookEmbed(
    string Title,
    string Description,
    int Color,
    IReadOnlyList<DiscordWebhookField> Fields,
    string Timestamp);
