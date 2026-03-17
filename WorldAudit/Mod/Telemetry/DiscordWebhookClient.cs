using System.Net.Http.Json;

namespace WorldAudit.Mod.Telemetry;

internal sealed class DiscordWebhookClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;

    public DiscordWebhookClient(string webhookUrl, TimeSpan timeout)
    {
        _webhookUrl = webhookUrl;
        _httpClient = new HttpClient
        {
            Timeout = timeout
        };
    }

    public Task<HttpResponseMessage> SendAsync(DiscordWebhookMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = new
        {
            username = message.Username,
            content = message.Content,
            embeds = message.Embeds.Select(embed => new
            {
                title = embed.Title,
                description = embed.Description,
                color = embed.Color,
                timestamp = embed.Timestamp,
                fields = embed.Fields.Select(field => new
                {
                    name = field.Name,
                    value = field.Value,
                    inline = field.Inline
                })
            })
        };

        return _httpClient.PostAsJsonAsync(_webhookUrl, payload, cancellationToken);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
