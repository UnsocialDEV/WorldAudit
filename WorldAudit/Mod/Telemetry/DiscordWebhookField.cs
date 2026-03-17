namespace WorldAudit.Mod.Telemetry;

internal sealed record DiscordWebhookField(string Name, string Value, bool Inline = false);
