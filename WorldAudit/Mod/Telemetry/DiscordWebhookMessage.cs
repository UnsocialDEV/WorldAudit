namespace WorldAudit.Mod.Telemetry;

internal sealed record DiscordWebhookMessage(
    string Content,
    string Username,
    IReadOnlyList<DiscordWebhookEmbed> Embeds);
